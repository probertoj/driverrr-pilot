using System.Management;

namespace Driverrr;

/// <summary>Enumera los dispositivos del PC y el driver que usa cada uno (vía WMI).</summary>
public static class DeviceScanner
{
    public static List<DeviceItem> Scan()
    {
        // 1) Todos los Hardware IDs y el código de error de cada dispositivo.
        var entities = new Dictionary<string, (string[] Ids, int Error)>(StringComparer.OrdinalIgnoreCase);
        using (var s = new ManagementObjectSearcher(
                   "SELECT DeviceID,HardwareID,ConfigManagerErrorCode FROM Win32_PnPEntity"))
        {
            foreach (ManagementBaseObject o in s.Get())
            {
                var id = o["DeviceID"] as string;
                if (id == null) continue;
                entities[id] = (o["HardwareID"] as string[] ?? Array.Empty<string>(),
                                Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0));
            }
        }

        // 2) El driver instalado en cada dispositivo.
        var list = new List<DeviceItem>();
        using (var s = new ManagementObjectSearcher(
                   "SELECT DeviceID,DeviceName,DeviceClass,Manufacturer,DriverProviderName,DriverVersion,DriverDate,InfName,HardWareID FROM Win32_PnPSignedDriver"))
        {
            foreach (ManagementBaseObject o in s.Get())
            {
                var id = o["DeviceID"] as string ?? "";
                var name = o["DeviceName"] as string;
                var version = o["DriverVersion"] as string ?? "";
                entities.TryGetValue(id, out var ent);

                // Código 22 = desactivado por el usuario: no es un problema.
                bool problem = ent.Error != 0 && ent.Error != 22;
                if (string.IsNullOrWhiteSpace(name) && !problem) continue;
                if (version == "" && !problem) continue;

                // El registro manda sobre WMI, que puede ir atrasado tras una instalación reciente.
                var live = ReadLiveDriver(id);
                if (live != null) version = live.Value.Version;

                var ids = (ent.Ids ?? Array.Empty<string>()).ToList();
                if (ids.Count == 0 && o["HardWareID"] is string single) ids.Add(single);

                list.Add(new DeviceItem
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Dispositivo desconocido" : name!,
                    DeviceClass = o["DeviceClass"] as string ?? "",
                    Manufacturer = o["Manufacturer"] as string ?? "",
                    Provider = o["DriverProviderName"] as string ?? "",
                    Version = version,
                    Date = live?.Date ?? ParseDate(o["DriverDate"] as string),
                    InfName = live is { Inf: not "" } ? live.Value.Inf : o["InfName"] as string ?? "",
                    DeviceId = id,
                    HardwareIds = ids,
                    ErrorCode = ent.Error,
                    State = problem ? DeviceState.Problem : DeviceState.UpToDate
                });
            }
        }

        return list.OrderBy(d => d.DeviceClass).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Asocia cada actualización a su dispositivo por Hardware ID. Las que no casan se añaden como filas propias.</summary>
    public static void AttachUpdates(List<DeviceItem> devices, IEnumerable<DriverUpdate> updates)
    {
        foreach (var u in updates)
        {
            var matches = u.HardwareId == ""
                ? new List<DeviceItem>()
                : devices.Where(d => d.Update == null &&
                                     d.HardwareIds.Any(h => h.Equals(u.HardwareId, StringComparison.OrdinalIgnoreCase)))
                         .ToList();

            if (matches.Count == 0)
            {
                devices.Add(new DeviceItem
                {
                    Name = u.Model != "" ? u.Model : u.Title,
                    DeviceClass = u.DriverClass,
                    Provider = u.Provider,
                    Update = u,
                    State = DeviceState.UpdateAvailable
                });
                continue;
            }

            foreach (var d in matches)
            {
                d.Update = u;
                d.State = DeviceState.UpdateAvailable;
            }
        }
    }

    /// <summary>
    /// Une en una sola fila las instancias idénticas de un dispositivo (mismo nombre, driver y actualización):
    /// comparten paquete, así que se actualizan juntas.
    /// </summary>
    public static List<DeviceItem> Collapse(List<DeviceItem> devices) =>
        devices
            .GroupBy(d => (d.Name, d.InfName, d.Version, d.State, d.ErrorCode, Update: d.Update?.UpdateId))
            .Select(g =>
            {
                var first = g.First();
                first.Count = g.Count();
                return first;
            })
            .ToList();

    /// <summary>Versión del driver que usa ahora mismo un dispositivo, o null si no se puede leer.</summary>
    public static string? InstalledVersion(string deviceId) => ReadLiveDriver(deviceId)?.Version;

    /// <summary>
    /// Driver realmente asignado a un dispositivo, leído del registro de Windows.
    /// WMI (Win32_PnPSignedDriver) guarda en caché y sigue devolviendo el driver anterior tras una instalación.
    /// </summary>
    public static (string Version, string Inf, DateTime? Date)? ReadLiveDriver(string deviceId)
    {
        try
        {
            using var dev = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + deviceId);
            if (dev?.GetValue("Driver") is not string driverKey) return null;
            using var drv = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
            if (drv?.GetValue("DriverVersion") is not string version) return null;

            DateTime? date = DateTime.TryParse(drv.GetValue("DriverDate") as string,
                System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                System.Globalization.DateTimeStyles.None, out var d) ? d : null;
            return (version, drv.GetValue("InfPath") as string ?? "", date);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Texto corto que identifica el equipo: «HP Pavilion 15 · Windows 11 Home».</summary>
    public static string MachineSummary()
    {
        string First(string query, params string[] props)
        {
            try
            {
                using var s = new ManagementObjectSearcher(query);
                foreach (ManagementBaseObject o in s.Get())
                    return string.Join(" ", props.Select(p => (o[p] as string ?? "").Trim()).Where(v => v != ""));
            }
            catch { }
            return "";
        }

        var pc = First("SELECT Manufacturer,Model FROM Win32_ComputerSystem", "Manufacturer", "Model");
        var os = First("SELECT Caption FROM Win32_OperatingSystem", "Caption").Replace("Microsoft ", "");
        return string.Join("  ·  ", new[] { pc, os }.Where(v => v != ""));
    }

    static DateTime? ParseDate(string? cim)
    {
        if (string.IsNullOrEmpty(cim)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(cim); }
        catch { return null; }
    }
}
