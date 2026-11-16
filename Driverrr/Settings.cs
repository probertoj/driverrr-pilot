using System.IO;
using System.Text.Json;

namespace Driverrr;

/// <summary>Preferencias del usuario, guardadas en su carpeta. No contienen nada sensible.</summary>
public class Settings
{
    /// <summary>Consultar GitHub al abrir para avisar de versiones nuevas.</summary>
    public bool CheckForUpdates { get; set; } = true;

    static string FilePath => Path.Combine(SecurePaths.UserRoot, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); } catch { }
    }
}
