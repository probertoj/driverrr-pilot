using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Driverrr;

public enum DeviceState { UpToDate, UpdateAvailable, Problem, Working, Installed, Failed }

public enum UpdateSource { WindowsUpdate, Catalog }

/// <summary>Un driver más nuevo, ofrecido por Windows Update o encontrado en el Catálogo de Microsoft Update.</summary>
public class DriverUpdate
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public UpdateSource Source { get; set; }
    public string Version { get; set; } = "";
    public string Title { get; set; } = "";

    [JsonIgnore] public string SourceText =>
        Source == UpdateSource.Catalog ? "Catálogo de Microsoft" : "Windows Update";
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "";
    public string DriverClass { get; set; } = "";
    public string HardwareId { get; set; } = "";
    public DateTime? Date { get; set; }
    public long SizeBytes { get; set; }
    public string UpdateId { get; set; } = "";

    /// <summary>Objeto COM IUpdate, necesario para descargar e instalar.</summary>
    [JsonIgnore] public object? ComUpdate { get; set; }

    public string SizeText =>
        SizeBytes <= 0 ? ""
        : SizeBytes < 1024 * 1024 ? $"{Math.Max(1, SizeBytes / 1024)} KB"
        : $"{SizeBytes / 1024.0 / 1024.0:0.#} MB";
}

/// <summary>Un dispositivo del PC con su driver instalado y, si existe, la actualización disponible.</summary>
public class DeviceItem : INotifyPropertyChanged
{
    static readonly Brush Green = Frozen("#1E8E5A"), Orange = Frozen("#C9710A"), Red = Frozen("#C5352B"), Blue = Frozen("#2563EB");
    static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    public string Name { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTime? Date { get; set; }
    public string InfName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public List<string> HardwareIds { get; set; } = new();
    public int ErrorCode { get; set; }
    public DriverUpdate? Update { get; set; }

    /// <summary>Cuántas instancias idénticas representa esta fila.</summary>
    public int Count { get; set; } = 1;
    [JsonIgnore] public string DisplayName => Count > 1 ? $"{Name}  ×{Count}" : Name;

    DeviceState _state;
    string? _message;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DeviceState State
    {
        get => _state;
        set { _state = value; Notify(null); }
    }

    /// <summary>Texto libre que sustituye al estado (progreso, error concreto).</summary>
    [JsonIgnore] public string? Message
    {
        get => _message;
        set { _message = value; Notify(null); }
    }

    public bool NeedsAttention => State != DeviceState.UpToDate;
    [JsonIgnore] public bool CanUpdate => Update != null && State is DeviceState.UpdateAvailable or DeviceState.Failed;

    // ----- Textos para la interfaz -----
    static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DISPLAY"] = "Pantalla y gráficos", ["MONITOR"] = "Monitores", ["MEDIA"] = "Sonido",
        ["AUDIOENDPOINT"] = "Sonido", ["AUDIOPROCESSINGOBJECT"] = "Sonido", ["NET"] = "Red",
        ["BLUETOOTH"] = "Bluetooth", ["USB"] = "USB", ["HIDCLASS"] = "Teclado, ratón y entrada",
        ["KEYBOARD"] = "Teclado, ratón y entrada", ["MOUSE"] = "Teclado, ratón y entrada",
        ["SYSTEM"] = "Sistema y placa base", ["PROCESSOR"] = "Procesador", ["FIRMWARE"] = "Firmware",
        ["DISKDRIVE"] = "Almacenamiento", ["SCSIADAPTER"] = "Almacenamiento", ["HDC"] = "Almacenamiento",
        ["VOLUME"] = "Almacenamiento", ["CAMERA"] = "Cámara", ["IMAGE"] = "Cámara",
        ["PRINTER"] = "Impresoras", ["PRINTQUEUE"] = "Impresoras", ["BATTERY"] = "Batería",
        ["BIOMETRIC"] = "Seguridad", ["SECURITYDEVICES"] = "Seguridad",
        ["SOFTWARECOMPONENT"] = "Componentes de software", ["SOFTWAREDEVICE"] = "Componentes de software",
        ["EXTENSION"] = "Componentes de software"
    };

    [JsonIgnore] public string Category =>
        string.IsNullOrWhiteSpace(DeviceClass) ? "Otros"
        : Categories.TryGetValue(DeviceClass, out var c) ? c : "Otros";

    /// <summary>Versión nueva, extraída del título de Windows Update: «… Driver Update (1.2.3.4)».</summary>
    [JsonIgnore] public string NewVersion
    {
        get
        {
            if (Update == null) return "";
            if (Update.Version != "") return Update.Version;
            var m = System.Text.RegularExpressions.Regex.Match(Update.Title, @"\(([\d.]+)\)\s*$");
            return m.Success ? m.Groups[1].Value : "";
        }
    }

    [JsonIgnore] public string DetailText =>
        string.Join("  ·  ", new[]
        {
            Provider,
            string.IsNullOrEmpty(Version) ? "Sin driver instalado" : "Versión " + Version,
            Date?.ToString("dd/MM/yyyy") ?? ""
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    [JsonIgnore] public string NewVersionText =>
        Update == null ? "" :
        "Nueva versión " + string.Join("  ·  ", new[]
        {
            NewVersion != "" ? NewVersion : Update.Title, Update.Date?.ToString("dd/MM/yyyy") ?? "", Update.SizeText,
            Update.SourceText
        }.Where(s => s != ""));

    [JsonIgnore] public string Subtitle =>
        string.Join("  ·  ", new[] { DeviceClass, Provider }.Where(s => !string.IsNullOrWhiteSpace(s)));

    [JsonIgnore] public string InstalledText =>
        string.IsNullOrEmpty(Version)
            ? "Sin driver instalado"
            : $"Instalado: {Version}" + (Date is { } d ? $"  ({d:dd/MM/yyyy})" : "");

    [JsonIgnore] public string AvailableText =>
        Update == null ? "" :
        "Disponible: " + string.Join("  ·  ", new[]
        {
            Update.Title, Update.Date?.ToString("dd/MM/yyyy") ?? "", Update.SizeText
        }.Where(s => s != ""));

    [JsonIgnore] public string StateText => Message ?? State switch
    {
        DeviceState.UpdateAvailable => "Actualización disponible",
        DeviceState.Problem => ErrorCode == 28 ? "Falta el driver" : $"Problema (código {ErrorCode})",
        DeviceState.Working => "Trabajando…",
        DeviceState.Installed => "Actualizado",
        DeviceState.Failed => "No se pudo instalar",
        _ => "Al día"
    };

    [JsonIgnore] public Brush StateBrush => State switch
    {
        DeviceState.UpdateAvailable => Orange,
        DeviceState.Problem or DeviceState.Failed => Red,
        DeviceState.Working => Blue,
        _ => Green
    };

    [JsonIgnore] public Visibility UpdateVisibility => CanUpdate ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility AvailableVisibility => Update != null ? Visibility.Visible : Visibility.Collapsed;
    [JsonIgnore] public Visibility VendorVisibility => VendorUrl != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Web oficial del fabricante para tarjetas gráficas (Windows Update suele ir atrasado con ellas).</summary>
    [JsonIgnore] public string? VendorUrl
    {
        get
        {
            if (!DeviceClass.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)) return null;
            var ids = string.Join(" ", HardwareIds).ToUpperInvariant();
            if (ids.Contains("VEN_10DE")) return "https://www.nvidia.com/Download/index.aspx";
            if (ids.Contains("VEN_1002")) return "https://www.amd.com/en/support";
            if (ids.Contains("VEN_8086")) return "https://www.intel.com/content/www/us/en/support/detect.html";
            return null;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
