using System.Diagnostics;
using System.IO;
using System.Management;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace DriverPilot;

public enum BackupResult { NotNeeded, Saved, Failed }

/// <summary>Red de seguridad antes de tocar drivers: punto de restauración y copia del driver actual.</summary>
public static class SafetyService
{
    /// <summary>Ruta de las copias (solo para mostrarla; la crea y protege la app elevada).</summary>
    public static string BackupRoot => Path.Combine(SecurePaths.MachineRoot, "Backups");

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Crea un punto de restauración del sistema. Devuelve null si va bien, o el motivo del fallo.</summary>
    public static string? CreateRestorePoint(string description)
    {
        try
        {
            using var cls = new ManagementClass(@"\\.\root\default:SystemRestore");
            const int DeviceDriverInstall = 10, BeginSystemChange = 100;
            var rc = Convert.ToUInt32(cls.InvokeMethod("CreateRestorePoint",
                new object[] { description, DeviceDriverInstall, BeginSystemChange }));
            return rc == 0 ? null : $"código {rc} (¿Restaurar sistema desactivado?)";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Solo los drivers de terceros (oemNN.inf) se pueden exportar; los de Windows no lo necesitan.</summary>
    public static bool CanBackup(DeviceItem device) =>
        Regex.IsMatch(device.InfName, @"^oem\d+\.inf$", RegexOptions.IgnoreCase);

    /// <summary>Exporta el paquete del driver actual a una carpeta protegida de la sesión de copia.</summary>
    public static (BackupResult Result, string? Folder) BackupDriver(DeviceItem device, string sessionFolder)
    {
        if (!CanBackup(device)) return (BackupResult.NotNeeded, null);
        try
        {
            var safeName = string.Concat(device.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            var dir = Path.Combine(sessionFolder, $"{safeName} ({device.InfName})");
            Directory.CreateDirectory(dir);

            var psi = new ProcessStartInfo(SecurePaths.SystemTool("pnputil.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("/export-driver");
            psi.ArgumentList.Add(device.InfName);
            psi.ArgumentList.Add(dir);

            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(60_000);

            bool ok = p.HasExited && p.ExitCode == 0 && Directory.EnumerateFiles(dir, "*.inf").Any();
            if (ok) RestoreService.WriteInfo(dir, device);
            return ok ? (BackupResult.Saved, dir) : (BackupResult.Failed, null);
        }
        catch
        {
            return (BackupResult.Failed, null);
        }
    }
}
