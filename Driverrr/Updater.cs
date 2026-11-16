using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Driverrr;

public record UpdateInfo(Version Version, string ExeUrl, string SumsUrl);

/// <summary>
/// Avisa de versiones nuevas publicadas en GitHub y, si el usuario acepta, actualiza la app instalada.
/// Nunca actualiza por su cuenta: hace falta un clic y el permiso de administrador de Windows.
/// La descarga se hace ya elevada, en una carpeta que solo un administrador puede tocar, y se verifica
/// antes de ejecutarla.
/// </summary>
public static class Updater
{
    const string Repo = "probertoj/driverrr-pilot";
    const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";

    const string ExeName = "Driverrr.exe", SumsName = "SHA256SUMS.txt";
    const long MaxExeBytes = 300L * 1024 * 1024;

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Driverrr/" + Installer.ThisVersion);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Devuelve la versión más nueva publicada, o null si ya se tiene la última.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version? current = null)
    {
        current ??= Version.Parse(Installer.ThisVersion);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var json = await Http.GetStringAsync(LatestApi, cts.Token);
        return ParseLatest(json, current);
    }

    /// <summary>Interpreta la respuesta de GitHub. Solo acepta una release con sus dos ficheros y enlaces del propio repositorio.</summary>
    internal static UpdateInfo? ParseLatest(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var m = Regex.Match(tag, @"^v(\d+\.\d+\.\d+)$");
        if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var version) || version <= current) return null;

        string? exe = null, sums = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == ExeName) exe = url;
            else if (name == SumsName) sums = url;
        }

        // Los enlaces deben ser exactamente los de esa versión de este repositorio.
        var expected = DownloadPrefix + tag + "/";
        if (exe != expected + ExeName || sums != expected + SumsName) return null;
        return new UpdateInfo(version, exe, sums);
    }

    /// <summary>Solo se descarga por HTTPS de GitHub o de sus servidores de ficheros.</summary>
    internal static bool IsTrustedGitHubUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo == "" &&
               (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extrae de «huella  fichero» la huella SHA-256 de un fichero concreto.</summary>
    internal static string? ParseSums(string text, string fileName)
    {
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
            if (m.Success && m.Groups[2].Value.Trim() == fileName) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Descarga la versión nueva a una carpeta y la verifica: origen, tamaño, huella publicada,
    /// y que el fichero sea de verdad esa versión de Driverrr. Devuelve la ruta del ejecutable.
    /// </summary>
    internal static async Task<string> DownloadVerifiedAsync(UpdateInfo info, string dir, Action<string>? progress = null)
    {
        if (!IsTrustedGitHubUri(info.ExeUrl) || !IsTrustedGitHubUri(info.SumsUrl))
            throw new InvalidOperationException("El enlace de descarga no es de GitHub.");

        progress?.Invoke("Leyendo la huella publicada…");
        var sumsText = await GetCheckedAsync(info.SumsUrl, 64 * 1024, null);
        var expected = ParseSums(System.Text.Encoding.ASCII.GetString(sumsText), ExeName)
                       ?? throw new InvalidOperationException("La versión publicada no trae la huella del ejecutable.");

        progress?.Invoke($"Descargando Driverrr {info.Version}…");
        var path = Path.Combine(dir, ExeName);
        await GetCheckedAsync(info.ExeUrl, MaxExeBytes, path);

        progress?.Invoke("Verificando…");
        await using (var f = File.OpenRead(path))
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(f)).ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("La descarga está dañada: la huella no coincide.");
        }

        var fvi = FileVersionInfo.GetVersionInfo(path);
        var product = fvi.ProductVersion?.Split('+')[0];
        if (fvi.ProductName != "Driverrr" || product != info.Version.ToString())
            throw new InvalidOperationException("El fichero descargado no es la versión anunciada de Driverrr.");
        return path;
    }

    /// <summary>Descarga con límite de tamaño, comprobando que tras las redirecciones el servidor sigue siendo de GitHub.</summary>
    static async Task<byte[]> GetCheckedAsync(string url, long maxBytes, string? saveTo)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (!IsTrustedGitHubUri(response.RequestMessage?.RequestUri?.ToString()))
            throw new InvalidOperationException("La descarga se redirigió fuera de GitHub.");
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidOperationException("El fichero es anormalmente grande.");

        await using var src = await response.Content.ReadAsStreamAsync();
        await using Stream dst = saveTo != null ? new FileStream(saveTo, FileMode.CreateNew, FileAccess.Write) : new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            if ((total += n) > maxBytes) throw new InvalidOperationException("El fichero es anormalmente grande.");
            await dst.WriteAsync(buffer.AsMemory(0, n));
        }
        return dst is MemoryStream ms ? ms.ToArray() : Array.Empty<byte>();
    }

    /// <summary>
    /// Actualiza la app instalada. Se ejecuta ya como administrador. Vuelve a consultar GitHub (no se fía de
    /// lo que vio la ventana sin elevar), descarga a una carpeta protegida, verifica y lanza el instalador
    /// de la versión nueva, que sustituye a esta al cerrarse. Devuelve null si va bien, o el motivo.
    /// </summary>
    public static async Task<string?> SelfUpdateAsync(Action<string> progress)
    {
        try
        {
            if (!SafetyService.IsAdmin()) return "Hacen falta permisos de administrador.";
            if (!Installer.RunningInstalled) return "Solo se actualiza la copia instalada.";

            progress("Buscando la última versión…");
            var info = await CheckAsync();
            if (info == null) return "Ya tienes la última versión.";

            SecurePaths.PurgeWork();
            var dir = SecurePaths.NewWorkDir();
            Log.Write($"=== Actualización de la app: {Installer.ThisVersion} -> {info.Version} ===");
            var exe = await DownloadVerifiedAsync(info, dir, progress);
            Log.Write("Descarga verificada; se lanza el instalador de la versión nueva");

            progress("Instalando…");
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--install");
            psi.ArgumentList.Add("--wait-pid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Actualización de la app: error " + ex.Message);
            return ex.Message;
        }
    }
}
