using System.IO;

namespace DriverPilot;

/// <summary>Registro de lo que hace la app, para poder diagnosticar una instalación después.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _path;

    /// <summary>
    /// Elevada: en la zona de máquina protegida (un programa sin elevar no puede redirigir la escritura).
    /// Sin elevar: en la carpeta del usuario.
    /// </summary>
    public static string FilePath
    {
        get
        {
            if (_path != null) return _path;
            try
            {
                _path = Path.Combine(SafetyService.IsAdmin() ? SecurePaths.MachineDir() : SecurePaths.UserRoot, "driverpilot.log");
            }
            catch
            {
                _path = Path.Combine(Path.GetTempPath(), "driverpilot.log");
            }
            return _path;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { }
    }
}
