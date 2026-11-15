using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace Driverrr;

/// <summary>
/// Instala la app en «Archivos de programa», donde solo un administrador puede modificarla.
/// Mientras viva en una carpeta del usuario (Descargas), cualquier programa puede cambiar el ejecutable
/// y heredar los permisos de administrador la próxima vez que se acepte el aviso de Windows.
/// </summary>
public static class Installer
{
    const string AppName = "Driverrr";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

    public static string InstalledExe => Path.Combine(InstallDir, AppName + ".exe");

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName + ".lnk");

    static string RunningDir => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>¿Esta instancia se está ejecutando desde la carpeta de instalación?</summary>
    public static bool RunningInstalled => RunningDir.Equals(InstallDir, StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>Versión de la copia instalada, o null si no hay.</summary>
    public static string? InstalledVersion =>
        IsInstalled ? FileVersionInfo.GetVersionInfo(InstalledExe).ProductVersion?.Split('+')[0] : null;

    public static string ThisVersion =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Copia la app, crea el acceso directo y la registra en Windows. Devuelve null si va bien, o el motivo.</summary>
    public static string? Install()
    {
        try
        {
            if (!SafetyService.IsAdmin()) return "Hacen falta permisos de administrador.";
            if (RunningInstalled) return "Esta copia ya es la instalada.";
            if (OtherInstancesFrom(InstallDir).Any())
                return "Driverrr instalado está abierto. Ciérralo y vuelve a intentarlo.";

            Log.Write($"=== Instalación de la app -> {InstallDir} (versión {ThisVersion}, fichero único: {IsSingleFile}) ===");
            if (IsSingleFile) CopySingleFile(Environment.ProcessPath!, InstallDir);
            else CopyApp(RunningDir, InstallDir);
            CreateShortcut(ShortcutPath, InstalledExe, InstallDir);
            Register();
            RemoveLegacyInstall();
            Log.Write("Instalación de la app: terminada");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Instalación de la app: error " + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>
    /// Retira la instalación hecha con el nombre antiguo de la app, para que no queden dos.
    /// Solo toca lo que esa instalación registró en su carpeta fija de «Archivos de programa».
    /// </summary>
    static void RemoveLegacyInstall()
    {
        const string legacy = SecurePaths.LegacyName;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), legacy);
            var key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + legacy;
            using (var reg = Registry.LocalMachine.OpenSubKey(key))
            {
                // Sin su entrada de registro apuntando a esa carpeta no se da por nuestra.
                if (reg?.GetValue("InstallLocation") is not string location ||
                    !location.TrimEnd('\\').Equals(dir, StringComparison.OrdinalIgnoreCase)) return;
            }
            if (OtherInstancesFrom(dir, legacy).Any())
            {
                Log.Write("Instalación antigua abierta: no se retira");
                return;
            }

            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), legacy + ".lnk");
            if (File.Exists(lnk)) File.Delete(lnk);
            Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            if (Directory.Exists(dir) && !SecurePaths.IsReparsePoint(dir)) Directory.Delete(dir, true);
            Log.Write("Instalación antigua (" + legacy + ") retirada");
        }
        catch (Exception ex)
        {
            Log.Write("No se pudo retirar la instalación antigua: " + ex.Message);
        }
    }

    /// <summary>
    /// La versión que se descarga es un único .exe: todo va dentro. En ese caso la carpeta desde la que se
    /// ejecuta es la del usuario (Descargas, Escritorio…) y NO se debe copiar entera, solo el ejecutable.
    /// </summary>
#pragma warning disable IL3000   // precisamente se usa que Location viene vacío en un ejecutable de fichero único
    internal static bool IsSingleFile => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>Instala un ejecutable de fichero único: la carpeta de destino queda solo con él.</summary>
    internal static void CopySingleFile(string exePath, string target)
    {
        if (Directory.Exists(target))
        {
            if (SecurePaths.IsReparsePoint(target)) throw new IOException($"«{target}» es un enlace; se rechaza por seguridad.");
            Directory.Delete(target, true);
        }
        Directory.CreateDirectory(target);
        File.Copy(exePath, Path.Combine(target, AppName + ".exe"), overwrite: false);
    }

    /// <summary>Sustituye el contenido de la carpeta de destino por una copia exacta de la de origen (compilación de desarrollo).</summary>
    internal static void CopyApp(string source, string target)
    {
        if (Directory.Exists(target))
        {
            if (SecurePaths.IsReparsePoint(target)) throw new IOException($"«{target}» es un enlace; se rechaza por seguridad.");
            Directory.Delete(target, true);   // no sigue enlaces: borra el enlace, no su destino
        }
        Directory.CreateDirectory(target);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (SecurePaths.IsReparsePoint(dir)) continue;
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (SecurePaths.IsReparsePoint(file)) continue;
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: false);
        }
    }

    internal static void CreateShortcut(string lnkPath, string targetExe, string workingDir)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("No se puede crear el acceso directo.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = targetExe;
        lnk.WorkingDirectory = workingDir;
        lnk.Description = "Mantiene al día los drivers del equipo";
        lnk.Save();
    }

    static void Register()
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", ThisVersion);
        key.SetValue("Publisher", "Uso personal");
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        long bytes = Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
    }

    /// <summary>
    /// Quita la app, el acceso directo y el registro. Conserva las copias de seguridad de drivers.
    /// La carpeta se borra justo después de cerrarse el programa, porque no puede borrarse a sí mismo.
    /// </summary>
    public static string? Uninstall()
    {
        try
        {
            if (!SafetyService.IsAdmin()) return "Hacen falta permisos de administrador.";
            if (OtherInstancesFrom(InstallDir).Any(p => p.Id != Environment.ProcessId))
                return "Hay otra ventana de Driverrr abierta. Ciérrala y vuelve a intentarlo.";

            Log.Write("=== Desinstalación de la app ===");
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

            if (Directory.Exists(InstallDir) && !SecurePaths.IsReparsePoint(InstallDir))
            {
                // cmd arranca en System32, así que «timeout» se resuelve ahí y no en otra carpeta.
                Process.Start(new ProcessStartInfo(SecurePaths.SystemTool("cmd.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = Environment.SystemDirectory,
                    Arguments = $"/c \"timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"\""
                });
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    static IEnumerable<Process> OtherInstancesFrom(string dir, string processName = AppName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            if (path != null && Path.GetDirectoryName(path)!.Equals(dir, StringComparison.OrdinalIgnoreCase)) yield return p;
        }
    }
}
