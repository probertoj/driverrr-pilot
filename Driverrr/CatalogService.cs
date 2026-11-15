using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Driverrr;

/// <summary>
/// Segunda fuente de drivers: el Catálogo de Microsoft Update (web pública de Microsoft).
/// Contiene todas las versiones certificadas de cada driver, también las que Windows Update no ofrece.
/// </summary>
public static class CatalogService
{
    const string BaseUrl = "https://www.catalog.update.microsoft.com/";
    const int MaxQueries = 80, Parallelism = 3;
    const int BreakerThreshold = 6;                       // fallos seguidos antes de dejar de insistir
    const long MaxPackageBytes = 1L * 1024 * 1024 * 1024; // ningún driver razonable pasa de 1 GB

    static readonly string[] TrustedSigners =
    {
        "Microsoft Windows Hardware Compatibility Publisher",
        "Microsoft Windows"
    };

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Driverrr/0.1");
        return c;
    }

    internal record Entry(string UpdateId, string Title, string Products, string Classification, DateTime? Date, Version Version, long Size);

    // ---------- Búsqueda ----------

    /// <summary>Consultas que fallaron en la última búsqueda, tras agotar los reintentos.</summary>
    public static int FailedQueries => _failedQueries;
    static int _failedQueries, _consecutiveFailures;

    /// <summary>Busca en el Catálogo una versión más nueva para los dispositivos que aún no tienen actualización.</summary>
    public static async Task<int> FindUpdatesAsync(List<DeviceItem> devices, Action<string>? progress = null)
    {
        _failedQueries = 0;
        _consecutiveFailures = 0;

        // Solo drivers de terceros: los que trae Windows los mantiene Windows Update.
        var candidates = devices
            .Where(d => d.Update == null && d.DeviceId != "" &&
                        !d.Provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                        Version.TryParse(d.Version, out _))
            .Select(d => (Device: d, Queries: QueryIds(d)))
            .Where(x => x.Queries.Count > 0)
            .Take(MaxQueries)
            .ToList();

        LoadDiskCache();

        // Varios dispositivos comparten chip: cada identificador se consulta una sola vez.
        var inFlight = new ConcurrentDictionary<string, Lazy<Task<List<Entry>>>>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(Parallelism);

        async Task<List<Entry>> Cached(string id)
        {
            return await inFlight.GetOrAdd(id, key => new Lazy<Task<List<Entry>>>(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    if (DiskCache.TryGetValue(key, out var hit) && DateTime.Now - hit.At < CacheLife) return hit.Entries;

                    // Sin red o con el Catálogo caído no tiene sentido reintentar 80 veces.
                    if (_consecutiveFailures >= BreakerThreshold) throw new HttpRequestException("Catálogo no disponible.");

                    var entries = await SearchWithRetryAsync(key);
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    DiskCache[key] = new CachedQuery(DateTime.Now, entries);
                    return entries;
                }
                catch
                {
                    Interlocked.Increment(ref _consecutiveFailures);
                    Interlocked.Increment(ref _failedQueries);   // no debe estropear el escaneo, pero se cuenta
                    return new List<Entry>();
                }
                finally { gate.Release(); }
            })).Value;
        }

        int found = 0, done = 0;
        await Task.WhenAll(candidates.Select(async c =>
        {
            var installed = Version.Parse(c.Device.Version);

            // Primero la variante exacta de este equipo; si no hay nada, el modelo genérico de chip.
            foreach (var id in c.Queries)
            {
                var best = Best(await Cached(id), installed, c.Device.Date);
                if (best == null) continue;

                c.Device.Update = new DriverUpdate
                {
                    Source = UpdateSource.Catalog,
                    Title = best.Title,
                    Version = best.Version.ToString(),
                    Date = best.Date,
                    SizeBytes = best.Size,
                    UpdateId = best.UpdateId,
                    HardwareId = id
                };
                c.Device.State = DeviceState.UpdateAvailable;
                Interlocked.Increment(ref found);
                break;
            }
            progress?.Invoke($"Consultando el Catálogo de Microsoft Update… {Interlocked.Increment(ref done)}/{candidates.Count}");
        }));

        SaveDiskCache();
        return found;
    }

    /// <summary>
    /// La mejor entrada que Windows aceptaría como mejora. Windows ordena los drivers primero por FECHA y
    /// después por versión, y hay fabricantes que cambian de numeración: un número mayor con fecha anterior
    /// no es una actualización (Windows lo añadiría al almacén y seguiría usando el actual).
    /// </summary>
    internal static Entry? Best(IEnumerable<Entry> entries, Version installed, DateTime? installedDate) =>
        entries.Where(e => IsValidUpdateId(e.UpdateId) && e.Version > installed && !IsOlder(e.Date, installedDate))
               .OrderByDescending(e => e.Date ?? DateTime.MinValue).ThenByDescending(e => e.Version)
               .FirstOrDefault();

    /// <summary>Margen de dos días: las fechas llegan de fuentes con distinto huso horario.</summary>
    internal static bool IsOlder(DateTime? candidate, DateTime? installed) =>
        candidate != null && installed != null && candidate.Value.Date < installed.Value.Date.AddDays(-2);

    static async Task<List<Entry>> SearchWithRetryAsync(string hardwareId, int attempts = 3)
    {
        // El Catálogo limita las peticiones seguidas: se reintenta con espera antes de darlo por perdido.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var html = await Http.GetStringAsync(BaseUrl + "Search.aspx?q=" + Uri.EscapeDataString(hardwareId));
                return ParseSearchHtml(html);
            }
            catch when (attempt < attempts) { await Task.Delay(3000 * attempt); }
        }
    }

    /// <summary>
    /// Identificadores con los que buscar, del más específico (variante del fabricante del PC)
    /// al genérico (modelo de chip).
    /// </summary>
    internal static List<string> QueryIds(DeviceItem d)
    {
        var ids = new List<string>();
        foreach (var raw in d.HardwareIds)
        {
            var bus = raw.Split('\\')[0].ToUpperInvariant();
            if (bus is not ("PCI" or "USB" or "HDAUDIO" or "INTELAUDIO" or "ACPI" or "SWC")) continue;
            if (bus == "ACPI" && raw.Contains("VEN_", StringComparison.OrdinalIgnoreCase)) continue;   // se prefiere ACPI\XXXX0000

            var specific = Regex.Replace(raw, @"&REV_[^&]*", "", RegexOptions.IgnoreCase);
            var generic = Regex.Replace(specific, @"&SUBSYS_[^&]*", "", RegexOptions.IgnoreCase);
            if (specific.Length < 12 && bus != "ACPI") continue;

            ids.Add(specific);
            if (!generic.Equals(specific, StringComparison.OrdinalIgnoreCase)) ids.Add(generic);
            break;
        }
        return ids;
    }

    /// <summary>Extrae los drivers válidos para este Windows de una página de resultados del Catálogo.</summary>
    internal static List<Entry> ParseSearchHtml(string html)
    {
        // Una página de error o de límite de peticiones llega con código 200:
        // solo vale si trae resultados o el aviso explícito de «sin resultados».
        if (!html.Contains("_R0\"") && !html.Contains("ctl00_catalogBody_noResultText"))
            throw new HttpRequestException("Respuesta inesperada del Catálogo.");

        var list = new List<Entry>();
        var (osName, osRelease) = CurrentOs();

        foreach (Match row in Regex.Matches(html, @"<tr id=""([0-9a-f\-]{36})_R\d+"".*?</tr>", RegexOptions.Singleline))
        {
            var body = row.Value;
            string Cell(int n) => Clean(Regex.Match(body, $@"_C{n}_R\d+"">(.*?)</td>", RegexOptions.Singleline).Groups[1].Value);

            var products = Cell(2);
            var classification = Cell(3);
            if (!classification.Contains("Driver", StringComparison.OrdinalIgnoreCase)) continue;
            if (!products.Contains(osName, StringComparison.OrdinalIgnoreCase)) continue;

            // «version 25H2 and later» no vale para un equipo en 24H2.
            var rel = Regex.Match(products, @"version (\d\dH\d)", RegexOptions.IgnoreCase);
            if (rel.Success && osRelease != null && string.CompareOrdinal(rel.Groups[1].Value.ToUpperInvariant(), osRelease) > 0) continue;

            if (!Version.TryParse(Cell(5), out var version)) continue;
            if (!IsValidUpdateId(row.Groups[1].Value)) continue;

            DateTime? date = DateTime.TryParse(Cell(4), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var dt) ? dt : null;
            long.TryParse(Regex.Match(body, @"_originalSize"">(\d+)<").Groups[1].Value, out var size);
            var title = Clean(Regex.Match(body, @"_link'.*?>(.*?)</a>", RegexOptions.Singleline).Groups[1].Value);

            list.Add(new Entry(row.Groups[1].Value, title, products, classification, date, version, size));
        }
        return list;
    }

    static string Clean(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", " ", RegexOptions.Singleline)), @"\s+", " ").Trim();

    static (string Name, string? Release) CurrentOs()
    {
        int build = Environment.OSVersion.Version.Build;
        if (build < 22000) return ("Windows 10", null);
        string? release = build switch
        {
            >= 26200 => "25H2",
            >= 26100 => "24H2",
            >= 22631 => "23H2",
            >= 22621 => "22H2",
            _ => "21H2"
        };
        return ("Windows 11", release);
    }

    // ---------- Validaciones de seguridad ----------

    /// <summary>El identificador acaba en una ruta de disco y en una petición web: solo se admite un GUID.</summary>
    internal static bool IsValidUpdateId(string? id) =>
        id != null && Regex.IsMatch(id, @"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$");

    /// <summary>Solo se descarga por HTTPS y de servidores de Microsoft.</summary>
    internal static bool IsTrustedDownloadUri(string? url, out Uri uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo == "" &&
               (uri.Host.EndsWith(".windowsupdate.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Algún INF del paquete debe nombrar un Hardware ID del dispositivo y servir para esta arquitectura.</summary>
    internal static bool PackageMatchesDevice(string folder, DeviceItem device)
    {
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "amd64"
        };

        foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(inf); } catch { continue; }
            if (!text.Contains(arch, StringComparison.OrdinalIgnoreCase)) continue;
            if (device.HardwareIds.Any(id => id.Length >= 8 && text.Contains(id, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    /// <summary>
    /// Todos los catálogos (.cat) del paquete deben ir firmados por Microsoft (certificación WHQL).
    /// Es una comprobación adicional: Windows valida la cadena de la firma al importar el driver.
    /// </summary>
    internal static bool IsSignedByMicrosoft(string folder, out string detail)
    {
        var cats = Directory.EnumerateFiles(folder, "*.cat", SearchOption.AllDirectories).ToList();
        if (cats.Count == 0) { detail = "el paquete no trae catálogo de firma"; return false; }

        foreach (var cat in cats)
        {
            string subject;
            try
            {
#pragma warning disable SYSLIB0057
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(cat));
#pragma warning restore SYSLIB0057
                subject = cert.GetNameInfo(X509NameType.SimpleName, false);
            }
            catch
            {
                detail = $"{Path.GetFileName(cat)} no está firmado";
                return false;
            }
            if (!TrustedSigners.Contains(subject, StringComparer.Ordinal))
            {
                detail = $"{Path.GetFileName(cat)} lo firma «{subject}», no Microsoft";
                return false;
            }
        }
        detail = "firmado por Microsoft";
        return true;
    }

    /// <summary>
    /// Vuelve a preguntar al Catálogo, sin caché, si de verdad ofrece esa actualización para ese dispositivo
    /// y si es más nueva que el driver instalado. Devuelve null si todo cuadra, o el motivo del rechazo.
    /// </summary>
    internal static async Task<string?> VerifyLiveAsync(DeviceItem device)
    {
        var update = device.Update;
        if (update == null || !IsValidUpdateId(update.UpdateId)) return "Identificador de actualización no válido.";
        if (!QueryIds(device).Contains(update.HardwareId, StringComparer.OrdinalIgnoreCase))
            return "La actualización no corresponde a este dispositivo.";

        if (!Version.TryParse(DeviceScanner.InstalledVersion(device.DeviceId) ?? device.Version, out var installed))
            return "No se pudo leer la versión instalada.";

        List<Entry> entries;
        try { entries = await SearchWithRetryAsync(update.HardwareId); }
        catch { return "No se pudo confirmar con el Catálogo de Microsoft; inténtalo en unos minutos."; }

        var live = entries.FirstOrDefault(e => e.UpdateId.Equals(update.UpdateId, StringComparison.OrdinalIgnoreCase));
        var installedDate = DeviceScanner.ReadLiveDriver(device.DeviceId)?.Date ?? device.Date;
        if (live == null || live.Version <= installed || live.Version.ToString() != update.Version || IsOlder(live.Date, installedDate))
            return "El Catálogo no ofrece esta versión para el dispositivo; vuelve a escanear.";
        return null;
    }

    /// <summary>Consulta en vivo, para pruebas: la mejor entrada del Catálogo para un identificador.</summary>
    internal static async Task<Entry?> BestLiveAsync(string hardwareId, Version installed) =>
        Best(await SearchWithRetryAsync(hardwareId), installed, null);

    // ---------- Descarga e instalación ----------

    /// <summary>Descarga el paquete, comprueba su integridad y que sirve para el dispositivo, y lo instala con pnputil.</summary>
    public static async Task<InstallOutcome> InstallAsync(DeviceItem device, Action<string> progress)
    {
        string? work = null;
        try
        {
            var update = device.Update!;
            if (!SafetyService.IsAdmin()) return Fail("Hacen falta permisos de administrador.");
            if (!IsValidUpdateId(update.UpdateId)) return Fail("Identificador de actualización no válido.");

            // Lo que se vio en el escaneo pudo salir de la caché, que cualquier programa del usuario puede alterar:
            // se vuelve a preguntar al Catálogo antes de instalar nada.
            progress("Confirmando con el Catálogo…");
            var rejected = await VerifyLiveAsync(device);
            if (rejected != null) return Fail(rejected);

            progress("Descargando…");
            var dialog = await Http.PostAsync(BaseUrl + "DownloadDialog.aspx", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["updateIDs"] = $"[{{\"size\":0,\"languages\":\"\",\"uidInfo\":\"{update.UpdateId}\",\"updateID\":\"{update.UpdateId}\"}}]"
            }));
            var js = await dialog.Content.ReadAsStringAsync();
            var url = Regex.Match(js, @"files\[0\]\.url\s*=\s*'([^']+)'").Groups[1].Value;
            var sha256 = Regex.Match(js, @"files\[0\]\.sha256\s*=\s*'([^']+)'").Groups[1].Value;

            if (!IsTrustedDownloadUri(url, out var uri))
                return Fail("El Catálogo no devolvió un enlace de descarga de Microsoft.");
            if (sha256 == "")
                return Fail("El Catálogo no indicó la huella del paquete; no se puede verificar.");

            // Carpeta nueva, solo accesible a administradores: nadie puede cambiar los ficheros entre la verificación y la instalación.
            work = SecurePaths.NewWorkDir();
            var files = Path.Combine(work, "files");
            Directory.CreateDirectory(files);
            var cab = Path.Combine(work, "driver.cab");

            using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                // Tras posibles redirecciones, el servidor final también debe ser de Microsoft.
                if (!IsTrustedDownloadUri(response.RequestMessage?.RequestUri?.ToString(), out _))
                    return Fail("La descarga se redirigió fuera de los servidores de Microsoft.");
                if (response.Content.Headers.ContentLength > MaxPackageBytes)
                    return Fail("El paquete es anormalmente grande; no se ha descargado.");

                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(cab, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    if ((total += n) > MaxPackageBytes) return Fail("El paquete es anormalmente grande; descarga cancelada.");
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                }
            }

            progress("Verificando…");
            await using (var f = File.OpenRead(cab))
            {
                var actual = Convert.ToBase64String(await SHA256.HashDataAsync(f));
                if (actual != sha256) return Fail("El paquete descargado está dañado (la huella no coincide).");
            }

            if (Run("expand.exe", cab, "-F:*", files).Code != 0)
                return Fail("No se pudo descomprimir el paquete.");

            bool signed = IsSignedByMicrosoft(files, out var signature);
            Log.Write($"Paquete: {uri} | sha256 ok | firma: {signature} | INF: " +
                      string.Join(", ", Directory.EnumerateFiles(files, "*.inf", SearchOption.AllDirectories).Select(Path.GetFileName)));
            if (!signed)
                return Fail("El paquete no está certificado por Microsoft: " + signature + ".");

            if (!PackageMatchesDevice(files, device))
                return Fail("El paquete no es para este dispositivo exacto; no se ha instalado.");

            progress("Instalando…");
            var (code, output) = Run("pnputil.exe", "/add-driver", Path.Combine(files, "*.inf"), "/subdirs", "/install");
            Log.Write($"pnputil código={code}\n{output.Trim()}");
            bool reboot = code == 3010;
            const int NotApplied = 259;   // el paquete entró en el almacén, pero Windows no lo aplicó a ningún dispositivo
            var now = DeviceScanner.InstalledVersion(device.DeviceId);
            bool unchanged = !reboot && now != null && now == device.Version;

            if (code == NotApplied || (code == 0 && unchanged))
            {
                // No dejar en el almacén de Windows un paquete que no se usa.
                var published = Regex.Match(output, @"\boem\d+\.inf\b", RegexOptions.IgnoreCase).Value;
                if (published != "")
                {
                    var del = Run("pnputil.exe", "/delete-driver", published);   // sin /force: si estuviera en uso, no se borra
                    Log.Write($"Retirado del almacén {published}: código={del.Code}");
                }
                return Fail("Windows considera que el driver que ya tienes es igual o mejor; no se ha cambiado nada.");
            }
            if (code != 0 && !reboot)
                return Fail(LastLine(output) ?? $"pnputil terminó con código {code}");

            return new InstallOutcome(true, reboot, null);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
        finally
        {
            // El paquete ya está en el almacén de drivers de Windows; la copia de trabajo sobra.
            try { if (work != null && Directory.Exists(work)) Directory.Delete(work, true); } catch { }
        }

        static InstallOutcome Fail(string why) => new(false, false, why);
    }

    internal static (int Code, string Output) Run(string exe, params string[] args)
    {
        // Ruta completa a System32: nunca se busca el ejecutable junto a la app ni en la carpeta actual.
        var psi = new ProcessStartInfo(SecurePaths.SystemTool(exe))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    static string? LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l != "");

    // ---------- Caché en disco: evita repetir consultas (el Catálogo limita las peticiones seguidas) ----------
    // Vive en la carpeta del usuario y NO es de fiar: solo acelera el escaneo. Antes de instalar se revalida en vivo.

    record CachedQuery(DateTime At, List<Entry> Entries);
    static readonly TimeSpan CacheLife = TimeSpan.FromHours(12);
    static ConcurrentDictionary<string, CachedQuery> DiskCache = new(StringComparer.OrdinalIgnoreCase);

    internal static string CachePath => Path.Combine(SecurePaths.UserRoot, "catalog-cache.json");

    static void LoadDiskCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, CachedQuery>>(File.ReadAllText(CachePath));
            if (data == null) return;
            DiskCache = new(
                data.Where(p => p.Value?.Entries != null && p.Value.Entries.All(e => e?.Version != null && IsValidUpdateId(e.UpdateId))),
                StringComparer.OrdinalIgnoreCase);
        }
        catch { DiskCache = new(StringComparer.OrdinalIgnoreCase); }
    }

    static void SaveDiskCache()
    {
        try
        {
            File.WriteAllText(CachePath, JsonSerializer.Serialize(DiskCache.ToDictionary(p => p.Key, p => p.Value)));
        }
        catch { }
    }
}
