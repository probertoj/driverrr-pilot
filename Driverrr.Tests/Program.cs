// Pruebas de las comprobaciones de seguridad y del análisis del Catálogo.
// Uso:  Driverrr.Tests.exe [carpeta con páginas reales del Catálogo]
// No instala nada ni necesita administrador. Todo lo que crea vive en una carpeta temporal que borra al acabar.
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security;
using Driverrr;

int passed = 0, failed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) passed++; else failed++;
    Console.WriteLine($"{(ok ? "OK   " : "FALLA")} {name}{(detail != "" ? "  [" + detail + "]" : "")}");
}

bool Throws<T>(Action a) where T : Exception
{
    try { a(); return false; } catch (T) { return true; } catch { return false; }
}

var tmp = Path.Combine(Path.GetTempPath(), "DriverrrTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tmp);
try
{
    Console.WriteLine("== Identificador de actualización (acaba en rutas y peticiones web)");
    Check("acepta un GUID", CatalogService.IsValidUpdateId("a8ad9459-2fba-4be9-b8b2-9533bfa0b824"));
    Check("rechaza salto de carpeta", !CatalogService.IsValidUpdateId(@"..\..\Windows"));
    Check("rechaza GUID con ruta añadida", !CatalogService.IsValidUpdateId(@"a8ad9459-2fba-4be9-b8b2-9533bfa0b824\..\x"));
    Check("rechaza inyección en JSON", !CatalogService.IsValidUpdateId("a8ad9459\",\"x\":\""));
    Check("rechaza vacío y nulo", !CatalogService.IsValidUpdateId("") && !CatalogService.IsValidUpdateId(null));

    Console.WriteLine("== Origen de la descarga");
    Check("acepta servidor de Windows Update", CatalogService.IsTrustedDownloadUri("https://catalog.s.download.windowsupdate.com/d/x.cab", out _));
    Check("acepta download.microsoft.com", CatalogService.IsTrustedDownloadUri("https://download.microsoft.com/x.cab", out _));
    Check("rechaza HTTP sin cifrar", !CatalogService.IsTrustedDownloadUri("http://catalog.s.download.windowsupdate.com/x.cab", out _));
    Check("rechaza dominio parecido", !CatalogService.IsTrustedDownloadUri("https://windowsupdate.com.evil.example/x.cab", out _));
    Check("rechaza sufijo pegado", !CatalogService.IsTrustedDownloadUri("https://evilwindowsupdate.com/x.cab", out _));
    Check("rechaza usuario@host", !CatalogService.IsTrustedDownloadUri("https://download.microsoft.com@evil.example/x.cab", out _));
    Check("rechaza ruta local", !CatalogService.IsTrustedDownloadUri(@"C:\x.cab", out _));
    Check("rechaza file://", !CatalogService.IsTrustedDownloadUri("file://download.microsoft.com/x.cab", out _));

    Console.WriteLine("== Carpetas de trabajo de la app elevada");
    var real = Directory.CreateDirectory(Path.Combine(tmp, "real")).FullName;
    var link = Path.Combine(tmp, "link");
    var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
    mk.WaitForExit();
    Check("detecta un enlace (junction)", Directory.Exists(link) && SecurePaths.IsReparsePoint(link));
    Check("rechaza una carpeta que es un enlace", Throws<SecurityException>(() => SecurePaths.AssertTrusted(link)));
    Check("rechaza una raíz que es un enlace", Throws<SecurityException>(() => SecurePaths.HardenRoot(link)));
    Check("rechaza una carpeta creada por un usuario normal", SafetyService.IsAdmin() || Throws<SecurityException>(() => SecurePaths.AssertTrusted(real)),
        SafetyService.IsAdmin() ? "omitida: la prueba corre elevada" : "");
    Check("no se ha escrito nada a través del enlace", !Directory.EnumerateFileSystemEntries(real).Any());
    Check("herramientas del sistema con ruta completa",
        SecurePaths.SystemTool("pnputil.exe").Equals(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), StringComparison.OrdinalIgnoreCase)
        && File.Exists(SecurePaths.SystemTool("pnputil.exe")) && File.Exists(SecurePaths.SystemTool("expand.exe")));

    Console.WriteLine("== El paquete debe ser para el dispositivo exacto");
    var dev = new DeviceItem { HardwareIds = { @"PCI\VEN_8086&DEV_43E0&SUBSYS_12345678", @"PCI\VEN_8086&DEV_43E0" } };
    string Pkg(string name, string inf)
    {
        var d = Directory.CreateDirectory(Path.Combine(tmp, name)).FullName;
        File.WriteAllText(Path.Combine(d, "x.inf"), inf);
        return d;
    }
    Check("acepta INF con el Hardware ID y la arquitectura",
        CatalogService.PackageMatchesDevice(Pkg("ok", "[Mfg.NTamd64]\n%d%=I, PCI\\VEN_8086&DEV_43E0\n"), dev));
    Check("acepta INF en UTF-16", ((Func<bool>)(() =>
    {
        var d = Directory.CreateDirectory(Path.Combine(tmp, "u16")).FullName;
        File.WriteAllText(Path.Combine(d, "x.inf"), "[Mfg.NTamd64]\n%d%=I, pci\\ven_8086&dev_43e0\n", System.Text.Encoding.Unicode);
        return CatalogService.PackageMatchesDevice(d, dev);
    }))());
    Check("rechaza INF de otro dispositivo",
        !CatalogService.PackageMatchesDevice(Pkg("other", "[Mfg.NTamd64]\n%d%=I, PCI\\VEN_8086&DEV_9999\n"), dev));
    Check("rechaza INF de otra arquitectura",
        !CatalogService.PackageMatchesDevice(Pkg("arch", "[Mfg.NTarm64]\n%d%=I, PCI\\VEN_8086&DEV_43E0\n"), dev));
    Check("rechaza paquete sin INF", !CatalogService.PackageMatchesDevice(Directory.CreateDirectory(Path.Combine(tmp, "empty")).FullName, dev));

    Console.WriteLine("== Firma del paquete");
    Check("rechaza paquete sin catálogo", !CatalogService.IsSignedByMicrosoft(Pkg("nocat", "x"), out var why1), why1);
    var fake = Pkg("fakecat", "x");
    File.WriteAllText(Path.Combine(fake, "x.cat"), "esto no es un catálogo firmado");
    Check("rechaza catálogo sin firma", !CatalogService.IsSignedByMicrosoft(fake, out var why2), why2);
    var store = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
    // Un paquete real cualquiera del almacén de drivers de este equipo que traiga su catálogo.
    var realPkg = Directory.EnumerateDirectories(store).Take(400)
        .FirstOrDefault(d => Directory.EnumerateFiles(d, "*.cat").Any() && CatalogService.IsSignedByMicrosoft(d, out _));
    if (realPkg != null)
        Check("acepta un driver WHQL real del almacén de Windows", CatalogService.IsSignedByMicrosoft(realPkg, out var why3), why3);
    else Console.WriteLine("  (omitido: no hay paquetes con catálogo en el almacén)");

    // Un ejecutable cualquiera de este equipo firmado por alguien que no sea Microsoft.
    static string? Signer(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var c = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            return c.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        }
        catch { return null; }
    }
    var notMs = Directory.EnumerateFiles(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "*.exe", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 })
        .Take(400).FirstOrDefault(f => Signer(f) is { } s && !s.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase));
    if (notMs == null) Console.WriteLine("  (omitido: no se encontró un ejecutable firmado por un tercero)");
    else
    {
        var d = Directory.CreateDirectory(Path.Combine(tmp, "thirdparty")).FullName;
        File.Copy(notMs, Path.Combine(d, "x.cat"));
        Check("rechaza un fichero firmado por un tercero", !CatalogService.IsSignedByMicrosoft(d, out _));
    }

    Console.WriteLine("== Identificadores de búsqueda");
    var q = CatalogService.QueryIds(new DeviceItem { HardwareIds = { @"PCI\VEN_10EC&DEV_8168&SUBSYS_12345678&REV_16" } });
    Check("PCI: primero la variante del equipo, luego el chip genérico",
        q.SequenceEqual(new[] { @"PCI\VEN_10EC&DEV_8168&SUBSYS_12345678", @"PCI\VEN_10EC&DEV_8168" }), string.Join(" | ", q));
    Check("audio Intel incluido", CatalogService.QueryIds(new DeviceItem { HardwareIds = { @"INTELAUDIO\FUNC_01&VEN_10EC&DEV_1220&SUBSYS_56781234&REV_1000" } }).Count == 2);
    Check("componente de software incluido", CatalogService.QueryIds(new DeviceItem { HardwareIds = { @"SWC\VEN_DTSI&AID_DTSI2" } }).Count == 1);
    Check("bus no admitido: sin consultas", CatalogService.QueryIds(new DeviceItem { HardwareIds = { @"MONITOR\AOC2402", @"ROOT\x" } }).Count == 0);
    Check("sin Hardware IDs: sin consultas", CatalogService.QueryIds(new DeviceItem()).Count == 0);

    Console.WriteLine("== Análisis de páginas reales del Catálogo");
    var pages = args.Length > 0 ? args[0] : "";
    if (Directory.Exists(pages))
    {
        var full = CatalogService.ParseSearchHtml(File.ReadAllText(Path.Combine(pages, "r1.html")));
        Check("página con resultados: extrae drivers", full.Count > 0, $"{full.Count} válidos para este Windows");
        Check("todos con GUID, versión y tipo driver", full.All(e => CatalogService.IsValidUpdateId(e.UpdateId) && e.Version != null && e.Classification.Contains("Driver")));
        Check("ninguno de otra edición de Windows", full.All(e => e.Products.Contains(Environment.OSVersion.Version.Build >= 22000 ? "Windows 11" : "Windows 10")));
        Check("página «sin resultados»: lista vacía, sin error", CatalogService.ParseSearchHtml(File.ReadAllText(Path.Combine(pages, "none.html"))).Count == 0);
        Check("página de límite de peticiones (código 200, vacía): se trata como error",
            Throws<HttpRequestException>(() => CatalogService.ParseSearchHtml(File.ReadAllText(Path.Combine(pages, "r5.html")))));
    }
    else Console.WriteLine("  (omitido: no se indicó carpeta de páginas)");
    Console.WriteLine("== Qué cuenta como actualización (Windows ordena por fecha antes que por versión)");
    CatalogService.Entry E(string v, string date) => new(Guid.NewGuid().ToString(), "t", "Windows 11", "Drivers", DateTime.Parse(date), new Version(v), 1);
    var wiman = new[] { E("2543.98.178.0", "2025-10-19") };
    Check("caso real: número mayor pero fecha anterior NO es actualización",
        CatalogService.Best(wiman, new Version("267.102.85.0"), DateTime.Parse("2026-02-11")) == null);
    Check("versión y fecha posteriores sí lo son",
        CatalogService.Best(new[] { E("7.14.2.67", "2025-10-11") }, new Version("7.14.2.66"), DateTime.Parse("2025-08-17"))?.Version.ToString() == "7.14.2.67");
    Check("un día de diferencia por huso horario se tolera",
        CatalogService.Best(new[] { E("10.1.34.13", "1968-07-17") }, new Version("10.1.34.10"), DateTime.Parse("1968-07-18")) != null);
    Check("misma versión no es actualización",
        CatalogService.Best(new[] { E("1.0.0.0", "2026-01-01") }, new Version("1.0.0.0"), DateTime.Parse("2020-01-01")) == null);
    Check("entre varias gana la más reciente por fecha",
        CatalogService.Best(new[] { E("9.0.0.0", "2025-01-01"), E("3.0.0.0", "2026-06-01") }, new Version("1.0.0.0"), DateTime.Parse("2024-01-01"))?.Version.ToString() == "3.0.0.0");
    Check("sin fecha instalada conocida, decide la versión",
        CatalogService.Best(wiman, new Version("267.102.85.0"), null) != null);

    Console.WriteLine("== Instalador (sobre carpetas temporales; no toca Archivos de programa ni el registro)");
    var appSrc = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    var appDst = Path.Combine(tmp, "instalada");
    Directory.CreateDirectory(appDst);
    File.WriteAllText(Path.Combine(appDst, "fichero-de-version-anterior.txt"), "viejo");
    Installer.CopyApp(appSrc, appDst);
    string[] Rel(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f)).OrderBy(x => x).ToArray();
    Check("copia todos los ficheros de la app", Rel(appSrc).SequenceEqual(Rel(appDst)), $"{Rel(appDst).Length} ficheros");
    Check("los ficheros copiados son idénticos byte a byte", Rel(appSrc).All(f =>
        System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(appSrc, f)))
            .SequenceEqual(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(appDst, f))))));
    Check("no deja restos de una versión anterior", !File.Exists(Path.Combine(appDst, "fichero-de-version-anterior.txt")));
    Check("incluye el ejecutable y su configuración de arranque",
        File.Exists(Path.Combine(appDst, "Driverrr.exe")) && File.Exists(Path.Combine(appDst, "Driverrr.runtimeconfig.json")) && File.Exists(Path.Combine(appDst, "Driverrr.dll")));
    var linkDst = Path.Combine(tmp, "link-instalacion");
    Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkDst}\" \"{Path.Combine(tmp, "real")}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!.WaitForExit();
    Check("rechaza instalar sobre un enlace", Throws<IOException>(() => Installer.CopyApp(appSrc, linkDst)) && !Directory.EnumerateFileSystemEntries(Path.Combine(tmp, "real")).Any());
    if (Directory.Exists(linkDst)) Directory.Delete(linkDst);
    var lnk = Path.Combine(tmp, "Driverrr.lnk");
    Installer.CreateShortcut(lnk, Path.Combine(appDst, "Driverrr.exe"), appDst);
    dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
    string lnkTarget = sh.CreateShortcut(lnk).TargetPath;
    Check("el acceso directo apunta al ejecutable instalado", lnkTarget.Equals(Path.Combine(appDst, "Driverrr.exe"), StringComparison.OrdinalIgnoreCase), lnkTarget.Replace(tmp, "<tmp>"));
    Check("destino previsto: Archivos de programa", Installer.InstallDir.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Driverrr"), StringComparison.OrdinalIgnoreCase), Installer.InstallDir);
    Check("esta copia de pruebas no se considera instalada", !Installer.RunningInstalled);
    // Fichero único: aunque el .exe esté en una carpeta llena de cosas del usuario, solo se instala el .exe.
    var userFolder = Directory.CreateDirectory(Path.Combine(tmp, "descargas-del-usuario")).FullName;
    File.WriteAllText(Path.Combine(userFolder, "documento-privado.txt"), "no debe copiarse");
    Directory.CreateDirectory(Path.Combine(userFolder, "fotos"));
    File.Copy(Path.Combine(appSrc, "Driverrr.exe"), Path.Combine(userFolder, "Driverrr-descargado.exe"));
    var oneDst = Path.Combine(tmp, "instalada-fichero-unico");
    Installer.CopySingleFile(Path.Combine(userFolder, "Driverrr-descargado.exe"), oneDst);
    Check("fichero único: instala solo el ejecutable, nada más de la carpeta del usuario",
        Rel(oneDst).SequenceEqual(new[] { "Driverrr.exe" }), string.Join(", ", Rel(oneDst)));

    Console.WriteLine("== Actualizador de la app");
    const string dl = "https://github.com/probertoj/driverrr-pilot/releases/download/";
    string Release(string tag, string exeUrl, string sumsUrl, bool draft = false, bool pre = false) =>
        "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") +
        ",\"assets\":[{\"name\":\"Driverrr.exe\",\"browser_download_url\":\"" + exeUrl + "\"},{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"" + sumsUrl + "\"}]}";
    var v1 = new Version("1.0.0");
    var good = Updater.ParseLatest(Release("v1.2.0", dl + "v1.2.0/Driverrr.exe", dl + "v1.2.0/SHA256SUMS.txt"), v1);
    Check("detecta una versión más nueva", good?.Version == new Version("1.2.0"));
    Check("misma versión: no hay actualización", Updater.ParseLatest(Release("v1.0.0", dl + "v1.0.0/Driverrr.exe", dl + "v1.0.0/SHA256SUMS.txt"), v1) == null);
    Check("versión más antigua: no se ofrece (no hay vuelta atrás forzada)", Updater.ParseLatest(Release("v0.9.0", dl + "v0.9.0/Driverrr.exe", dl + "v0.9.0/SHA256SUMS.txt"), v1) == null);
    Check("ignora borradores y versiones preliminares",
        Updater.ParseLatest(Release("v2.0.0", dl + "v2.0.0/Driverrr.exe", dl + "v2.0.0/SHA256SUMS.txt", draft: true), v1) == null &&
        Updater.ParseLatest(Release("v2.0.0", dl + "v2.0.0/Driverrr.exe", dl + "v2.0.0/SHA256SUMS.txt", pre: true), v1) == null);
    Check("rechaza un ejecutable alojado fuera del repositorio",
        Updater.ParseLatest(Release("v2.0.0", "https://evil.example/Driverrr.exe", dl + "v2.0.0/SHA256SUMS.txt"), v1) == null);
    Check("rechaza un ejecutable de otro repositorio de GitHub",
        Updater.ParseLatest(Release("v2.0.0", "https://github.com/otro/repo/releases/download/v2.0.0/Driverrr.exe", dl + "v2.0.0/SHA256SUMS.txt"), v1) == null);
    Check("rechaza enlaces de otra versión distinta de la anunciada",
        Updater.ParseLatest(Release("v2.0.0", dl + "v1.5.0/Driverrr.exe", dl + "v2.0.0/SHA256SUMS.txt"), v1) == null);
    Check("rechaza etiquetas que no son una versión", Updater.ParseLatest(Release("ultima", dl + "ultima/Driverrr.exe", dl + "ultima/SHA256SUMS.txt"), v1) == null);
    Check("rechaza una release sin huella", Updater.ParseLatest("{\"tag_name\":\"v2.0.0\",\"assets\":[{\"name\":\"Driverrr.exe\",\"browser_download_url\":\"" + dl + "v2.0.0/Driverrr.exe\"}]}", v1) == null);
    Check("origen: acepta github.com y sus servidores de ficheros",
        Updater.IsTrustedGitHubUri("https://github.com/a/b") && Updater.IsTrustedGitHubUri("https://objects.githubusercontent.com/x") && Updater.IsTrustedGitHubUri("https://release-assets.githubusercontent.com/x"));
    Check("origen: rechaza HTTP, dominios parecidos y usuario@host",
        !Updater.IsTrustedGitHubUri("http://github.com/a") && !Updater.IsTrustedGitHubUri("https://github.com.evil.example/a") &&
        !Updater.IsTrustedGitHubUri("https://evilgithubusercontent.com/a") && !Updater.IsTrustedGitHubUri("https://github.com@evil.example/a") &&
        !Updater.IsTrustedGitHubUri("https://notgithub.com/a"));
    var h64 = new string('a', 64);
    Check("huella: la lee del formato publicado", Updater.ParseSums(h64 + "  Driverrr.exe\n", "Driverrr.exe") == h64);
    Check("huella: no confunde otro fichero ni una huella corta",
        Updater.ParseSums(h64 + "  Otro.exe\n", "Driverrr.exe") == null && Updater.ParseSums("abc123  Driverrr.exe", "Driverrr.exe") == null);
    try
    {
        Check("en vivo: con la versión actual no ofrece nada más antiguo", (await Updater.CheckAsync(new Version("99.0.0"))) == null);
        var live = await Updater.CheckAsync(new Version("0.1.0"));
        Check("en vivo: GitHub anuncia una versión posterior a la 0.1.0", live != null, live?.Version.ToString() ?? "");
        if (live != null)
        {
            var dlDir = Directory.CreateDirectory(Path.Combine(tmp, "actualizacion")).FullName;
            var got = await Updater.DownloadVerifiedAsync(live, dlDir);
            Check("en vivo: descarga la versión publicada y supera huella, producto y versión",
                File.Exists(got) && FileVersionInfo.GetVersionInfo(got).ProductName == "Driverrr", $"{new FileInfo(got).Length / 1024 / 1024} MB");
            var wrong = live with { Version = new Version("98.0.0") };
            var dlDir2 = Directory.CreateDirectory(Path.Combine(tmp, "actualizacion-falsa")).FullName;
            bool rejected = false;
            try { await Updater.DownloadVerifiedAsync(wrong, dlDir2); } catch (InvalidOperationException) { rejected = true; }
            Check("en vivo: rechaza un fichero que no es la versión anunciada", rejected);
        }
    }
    catch (Exception ex) { Console.WriteLine("  (omitido: GitHub no disponible ahora: " + ex.Message + ")"); }

    Console.WriteLine("== Cambio de nombre de la app: no se pierden los datos anteriores");
    Check("la zona de máquina apunta a una carpeta existente o a la nueva",
        Directory.Exists(SecurePaths.MachineRoot) || SecurePaths.MachineRoot.EndsWith(@"\Driverrr", StringComparison.OrdinalIgnoreCase), SecurePaths.MachineRoot);
    Check("sin elevar no se mueve ni se crea nada en ProgramData",
        SafetyService.IsAdmin() || !Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Driverrr"))
        || !Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), SecurePaths.LegacyName)));

    Console.WriteLine("== Restaurar: lectura de las copias");
    Check("nombre de carpeta de copia", RestoreService.ParseFolderName("DTS APO4x Service (oem171.inf)") == ("DTS APO4x Service", "oem171.inf"));
    Check("nombre con paréntesis propios", RestoreService.ParseFolderName("Intel(R) Watchdog Timer Driver (Intel(R) WDT) (oem37.inf)") == ("Intel(R) Watchdog Timer Driver (Intel(R) WDT)", "oem37.inf"));
    Check("rechaza carpeta que no es una copia", RestoreService.ParseFolderName("cualquier cosa") == null);
    Check("rechaza INF que no es de terceros", RestoreService.ParseFolderName(@"X (..\evil.inf)") == null && RestoreService.ParseFolderName("X (input.inf)") == null);
    var dv = RestoreService.ParseDriverVer("[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nDriverVer = 02/13/2026,1.16.5.0\r\n");
    Check("lee DriverVer de un INF", dv?.Version == "1.16.5.0" && dv?.Date == new DateTime(2026, 2, 13));
    Check("normaliza ceros a la izquierda", RestoreService.ParseDriverVer("DriverVer=02/02/2026,10.077.50.0807")?.Version == "10.77.50.807");
    Check("INF sin DriverVer", RestoreService.ParseDriverVer("[Version]\n") == null);
    Check("un driver inexistente no lo usa ningún dispositivo", RestoreService.DevicesUsing("oem999999.inf") == 0);
    Console.WriteLine("  -- copias reales restaurables en este equipo (solo lectura):");
    foreach (var r in RestoreService.List())
        Console.WriteLine($"     {r.DeviceName}: {r.CurrentVersion} ({r.CurrentInf}, lo usan {RestoreService.DevicesUsing(r.CurrentInf)}) -> {r.OldVersion} ({r.OldInf})  copia {r.SavedAt:HH:mm}");

    Console.WriteLine("== Revalidación en vivo antes de instalar (una consulta real al Catálogo)");
    const string query = @"PCI\VEN_10EC&DEV_8168";
    DeviceItem Nic(string updateId, string version, string hw = query) => new()
    {
        Version = "10.77.50.807",
        HardwareIds = { @"PCI\VEN_10EC&DEV_8168&REV_16" },
        Update = new DriverUpdate { Source = UpdateSource.Catalog, UpdateId = updateId, Version = version, HardwareId = hw }
    };
    try
    {
        var genuine = await CatalogService.BestLiveAsync(query, new Version("10.77.50.807"));
        if (genuine == null) Console.WriteLine("  (omitido: el Catálogo no devolvió nada más nuevo)");
        else
        {
            Check("acepta la actualización auténtica", await CatalogService.VerifyLiveAsync(Nic(genuine.UpdateId, genuine.Version.ToString())) == null, genuine.Version.ToString());
            await Task.Delay(1500);
            Check("rechaza un identificador inventado (caché falsificada)",
                await CatalogService.VerifyLiveAsync(Nic(Guid.NewGuid().ToString(), "99.0.0.0")) != null);
            await Task.Delay(1500);
            Check("rechaza versión falseada sobre un identificador real",
                await CatalogService.VerifyLiveAsync(Nic(genuine.UpdateId, "99.0.0.0")) != null);
        }
    }
    catch (Exception ex) { Console.WriteLine("  (omitido: Catálogo no disponible ahora: " + ex.Message + ")"); }
    Check("rechaza identificador con ruta, sin llegar a la red", await CatalogService.VerifyLiveAsync(Nic(@"..\..\x", "99.0.0.0")) != null);
    Check("rechaza consulta que no es del dispositivo, sin llegar a la red",
        await CatalogService.VerifyLiveAsync(Nic(Guid.NewGuid().ToString(), "99.0.0.0", @"PCI\VEN_8086&DEV_0001")) != null);

    Check("HTML basura: error, no lista vacía", Throws<HttpRequestException>(() => CatalogService.ParseSearchHtml("<html>proxy login</html>")));
    Check("cadena vacía: error", Throws<HttpRequestException>(() => CatalogService.ParseSearchHtml("")));
}
finally
{
    try
    {
        var link = Path.Combine(tmp, "link");
        if (Directory.Exists(link)) Directory.Delete(link);   // quita el enlace, no su destino
        Directory.Delete(tmp, true);
    }
    catch (Exception ex) { Console.WriteLine("AVISO limpieza: " + ex.Message); }
}

Console.WriteLine($"\n{passed} correctas, {failed} fallidas. Carpeta temporal borrada: {!Directory.Exists(tmp)}");
return failed == 0 ? 0 : 1;
