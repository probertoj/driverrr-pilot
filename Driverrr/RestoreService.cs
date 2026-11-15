using System.ComponentModel;
using System.IO;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace Driverrr;

/// <summary>Un driver que se cambió y del que hay copia para volver atrás.</summary>
public class RestoreItem : INotifyPropertyChanged
{
    public string DeviceName { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string BackupFolder { get; init; } = "";
    public string OldInf { get; init; } = "";
    public string OldVersion { get; init; } = "";
    public DateTime? OldDate { get; init; }
    public string CurrentInf { get; init; } = "";
    public string CurrentVersion { get; init; } = "";
    public DateTime SavedAt { get; init; }

    string? _message;
    bool _done;

    public string? Message { get => _message; set { _message = value; Notify(); } }
    public bool Done { get => _done; set { _done = value; Notify(); } }

    public string DetailText => $"Ahora: versión {CurrentVersion}";
    public string TargetText =>
        $"Volver a la versión {OldVersion}" + (OldDate is { } d ? $"  ·  {d:dd/MM/yyyy}" : "") + $"  ·  copia del {SavedAt:dd/MM/yyyy HH:mm}";
    public string StateText => Message ?? "";
    public Visibility ButtonVisibility => Done ? Visibility.Collapsed : Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>
/// Vuelve al driver anterior. Reinstalar la copia no basta: Windows elige siempre el driver más reciente,
/// así que hay que retirar el paquete nuevo para que recupere el antiguo (que se repone desde la copia si falta).
/// </summary>
public static class RestoreService
{
    record BackupInfo(string DeviceId, string Name, string Version);

    /// <summary>Deja junto a la copia a qué dispositivo pertenece, para poder restaurarla sin adivinar por el nombre.</summary>
    public static void WriteInfo(string backupDir, DeviceItem device)
    {
        try
        {
            File.WriteAllText(Path.Combine(backupDir, "driverrr-info.json"),
                JsonSerializer.Serialize(new BackupInfo(device.DeviceId, device.Name, device.Version)));
        }
        catch { }
    }

    /// <summary>Carpeta «Nombre del dispositivo (oemNN.inf)» → sus dos partes.</summary>
    internal static (string Name, string Inf)? ParseFolderName(string folder)
    {
        var m = Regex.Match(folder, @"^(.+) \((oem\d+\.inf)\)$", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant()) : null;
    }

    /// <summary>Lee «DriverVer = 02/13/2026,1.16.5.0» de un INF.</summary>
    internal static (DateTime? Date, string Version)? ParseDriverVer(string infText)
    {
        var m = Regex.Match(infText, @"^\s*DriverVer\s*=\s*([0-9/\-]+)\s*,\s*([0-9.]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (!m.Success) return null;
        DateTime? date = DateTime.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
        // «10.077.50.0807» y «10.77.50.807» son la misma versión: se normaliza para comparar con la del registro.
        var version = Version.TryParse(m.Groups[2].Value, out var v) ? v.ToString() : m.Groups[2].Value;
        return (date, version);
    }

    /// <summary>Copias que aún tiene sentido restaurar: el dispositivo existe y usa ahora otro driver. Solo lee.</summary>
    public static List<RestoreItem> List()
    {
        var items = new List<RestoreItem>();
        var root = SafetyService.BackupRoot;
        if (!Directory.Exists(root)) return items;

        Dictionary<string, List<string>>? idsByName = null;

        foreach (var session in Directory.EnumerateDirectories(root))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(session), "yyyy-MM-dd HH.mm.ss", null,
                    System.Globalization.DateTimeStyles.None, out var savedAt)) continue;

            foreach (var dir in Directory.EnumerateDirectories(session))
            {
                var parsed = ParseFolderName(Path.GetFileName(dir));
                if (parsed == null) continue;

                var inf = Directory.EnumerateFiles(dir, "*.inf").FirstOrDefault();
                if (inf == null) continue;
                (DateTime? Date, string Version)? ver;
                try { ver = ParseDriverVer(File.ReadAllText(inf)); } catch { continue; }
                if (ver == null) continue;

                // A qué dispositivo pertenece: lo dice la propia copia; en copias antiguas, el nombre si es único.
                string? deviceId = null, name = parsed.Value.Name;
                try
                {
                    // Las copias hechas con el nombre antiguo de la app guardaron el fichero con ese nombre.
                    var infoFile = new[] { "driverrr-info.json", SecurePaths.LegacyName.ToLowerInvariant() + "-info.json" }
                        .Select(n => Path.Combine(dir, n)).First(p => File.Exists(p));
                    var info = JsonSerializer.Deserialize<BackupInfo>(File.ReadAllText(infoFile));
                    if (info != null) { deviceId = info.DeviceId; name = info.Name; }
                }
                catch { }
                if (string.IsNullOrEmpty(deviceId))
                {
                    idsByName ??= DeviceIdsByName();
                    if (!idsByName.TryGetValue(parsed.Value.Name, out var ids) || ids.Count != 1) continue;
                    deviceId = ids[0];
                }

                var live = DeviceScanner.ReadLiveDriver(deviceId);
                if (live == null) continue;
                if (live.Value.Inf.Equals(parsed.Value.Inf, StringComparison.OrdinalIgnoreCase)) continue;   // ya usa ese
                if (live.Value.Version == ver.Value.Version) continue;
                if (!Regex.IsMatch(live.Value.Inf, @"^oem\d+\.inf$", RegexOptions.IgnoreCase)) continue;      // el actual es de Windows: no se retira

                items.Add(new RestoreItem
                {
                    DeviceName = name,
                    DeviceId = deviceId,
                    BackupFolder = dir,
                    OldInf = parsed.Value.Inf,
                    OldVersion = ver.Value.Version,
                    OldDate = ver.Value.Date,
                    CurrentInf = live.Value.Inf,
                    CurrentVersion = live.Value.Version,
                    SavedAt = savedAt
                });
            }
        }

        // Por dispositivo, solo el paso atrás más reciente.
        return items.GroupBy(i => i.DeviceId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.OrderByDescending(i => i.SavedAt).First())
                    .OrderByDescending(i => i.SavedAt)
                    .ToList();
    }

    static Dictionary<string, List<string>> DeviceIdsByName()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // Mismo origen de nombres que el escaneo, que es el que dio nombre a la carpeta de la copia.
        using var s = new ManagementObjectSearcher("SELECT DeviceID,DeviceName FROM Win32_PnPSignedDriver");
        foreach (ManagementBaseObject o in s.Get())
        {
            if (o["DeviceName"] is not string name || o["DeviceID"] is not string id) continue;
            var key = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
            list.Add(id);
        }
        return map;
    }

    /// <summary>
    /// Cuántos dispositivos usan un paquete de driver: retirarlo los afecta a todos, no solo al elegido.
    /// Cuenta por lo alto, porque el registro conserva entradas de dispositivos que ya no están conectados.
    /// </summary>
    public static int DevicesUsing(string inf)
    {
        int n = 0;
        try
        {
            using var classes = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class");
            if (classes == null) return 0;
            foreach (var guid in classes.GetSubKeyNames())
            {
                try
                {
                    using var cls = classes.OpenSubKey(guid);
                    if (cls == null) continue;
                    foreach (var instance in cls.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = cls.OpenSubKey(instance);
                            if ((key?.GetValue("InfPath") as string)?.Equals(inf, StringComparison.OrdinalIgnoreCase) == true) n++;
                        }
                        catch { }   // subclaves protegidas
                    }
                }
                catch { }
            }
        }
        catch { }
        return n;
    }

    /// <summary>Vuelve al driver anterior. Requiere administrador.</summary>
    public static InstallOutcome Restore(RestoreItem item, Action<string> progress)
    {
        try
        {
            if (!SafetyService.IsAdmin()) return Fail("Hacen falta permisos de administrador.");

            // La copia debe estar en la zona protegida: nada que haya podido plantar un programa sin permisos.
            var root = Path.GetFullPath(SafetyService.BackupRoot);
            var folder = Path.GetFullPath(item.BackupFolder);
            if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Fail("La copia no está en la carpeta de copias de Driverrr.");
            SecurePaths.HardenRoot(SecurePaths.MachineRoot);
            for (var p = folder; !p.Equals(SecurePaths.MachineRoot, StringComparison.OrdinalIgnoreCase); p = Path.GetDirectoryName(p)!)
                SecurePaths.AssertTrusted(p);

            // Se parte del estado real de ahora, no de lo que se vio al listar.
            var live = DeviceScanner.ReadLiveDriver(item.DeviceId);
            if (live == null) return Fail("El dispositivo ya no está presente.");
            var current = live.Value.Inf;
            if (!Regex.IsMatch(current, @"^oem\d+\.inf$", RegexOptions.IgnoreCase) || !Regex.IsMatch(item.OldInf, @"^oem\d+\.inf$", RegexOptions.IgnoreCase))
                return Fail("El driver actual es de Windows y no se puede retirar.");
            if (current.Equals(item.OldInf, StringComparison.OrdinalIgnoreCase) || live.Value.Version == item.OldVersion)
                return Fail("El dispositivo ya usa esa versión.");
            Log.Write($"--- Restaurar {item.DeviceName} | {live.Value.Version} ({current}) -> {item.OldVersion} ({item.OldInf}) | dev={item.DeviceId}");

            // El driver anterior tiene que estar disponible antes de quitar el actual.
            progress("Comprobando el driver anterior…");
            var store = CatalogService.Run("pnputil.exe", "/enum-drivers").Output;
            if (!Regex.IsMatch(store, $@"\b{Regex.Escape(item.OldInf)}\b", RegexOptions.IgnoreCase))
            {
                if (!CatalogService.IsSignedByMicrosoft(folder, out var signature))
                    return Fail("La copia no está certificada por Microsoft: " + signature + ".");
                var add = CatalogService.Run("pnputil.exe", "/add-driver", Path.Combine(folder, "*.inf"));
                Log.Write($"Repuesto desde la copia: código={add.Code}\n{add.Output.Trim()}");
                if (add.Code != 0 && add.Code != 259) return Fail("No se pudo reponer el driver anterior desde la copia.");
            }

            progress("Retirando el driver actual…");
            var del = CatalogService.Run("pnputil.exe", "/delete-driver", current, "/uninstall");
            Log.Write($"pnputil /delete-driver {current} /uninstall código={del.Code}\n{del.Output.Trim()}");
            bool reboot = del.Code == 3010;
            if (del.Code != 0 && !reboot)
                return Fail(del.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l != "") ?? $"pnputil terminó con código {del.Code}");

            // Que Windows vuelva a elegir driver para el dispositivo.
            CatalogService.Run("pnputil.exe", "/scan-devices");
            Thread.Sleep(2500);

            var now = DeviceScanner.ReadLiveDriver(item.DeviceId);
            Log.Write($"Resultado restauración: ahora={now?.Version ?? "?"} ({now?.Inf ?? "?"}) reinicio={reboot}");
            if (now == null && !reboot) return Fail("Se retiró el driver, pero el dispositivo quedó sin ninguno. Usa el punto de restauración.");
            if (now != null && now.Value.Version != item.OldVersion && !reboot)
                return new InstallOutcome(true, false, $"Windows eligió la versión {now.Value.Version}, no la {item.OldVersion}.");
            return new InstallOutcome(true, reboot, null);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        static InstallOutcome Fail(string why) => new(false, false, why);
    }
}
