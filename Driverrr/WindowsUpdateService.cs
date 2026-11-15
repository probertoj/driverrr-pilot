namespace Driverrr;

public record InstallOutcome(bool Success, bool RebootRequired, string? Error);

/// <summary>
/// Busca, descarga e instala drivers usando el agente de Windows Update (API COM pública de Windows).
/// Solo ofrece drivers firmados y publicados por Microsoft para este hardware.
/// </summary>
public class WindowsUpdateService
{
    const int ServerWindowsUpdate = 2;   // ssWindowsUpdate: ignora un posible WSUS corporativo
    const int ResultSucceeded = 2, ResultSucceededWithErrors = 3;

    dynamic? _session;

    dynamic Session
    {
        get
        {
            if (_session == null)
            {
                var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                           ?? throw new InvalidOperationException("El servicio de Windows Update no está disponible.");
                _session = Activator.CreateInstance(type)!;
                _session!.ClientApplicationID = "Driverrr";
            }
            return _session!;
        }
    }

    public List<DriverUpdate> Search()
    {
        dynamic searcher = Session.CreateUpdateSearcher();
        searcher.ServerSelection = ServerWindowsUpdate;
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");

        var list = new List<DriverUpdate>();
        int count = result.Updates.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic u = result.Updates.Item(i);
            var item = new DriverUpdate { ComUpdate = u, Title = Str(() => u.Title) };
            item.UpdateId = Str(() => u.Identity.UpdateID);
            item.Model = Str(() => u.DriverModel);
            item.Provider = Str(() => u.DriverProvider);
            item.DriverClass = Str(() => u.DriverClass);
            item.HardwareId = Str(() => u.DriverHardwareID);
            try { item.Date = (DateTime)u.DriverVerDate; } catch { }
            try { item.SizeBytes = (long)(decimal)u.MaxDownloadSize; } catch { }
            list.Add(item);
        }
        return list;
    }

    /// <summary>Descarga e instala un driver. Requiere permisos de administrador.</summary>
    public InstallOutcome Install(DriverUpdate update, Action<string> progress)
    {
        try
        {
            dynamic u = update.ComUpdate ?? throw new InvalidOperationException("Actualización no disponible.");
            // Aceptar una licencia es decisión del usuario: no se hace en su nombre.
            if (!(bool)u.EulaAccepted)
                return new InstallOutcome(false, false,
                    "Este driver pide aceptar una licencia. Instálalo desde Configuración > Windows Update > Actualizaciones opcionales.");

            var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!;
            dynamic coll = Activator.CreateInstance(collType)!;
            coll.Add(u);

            if (!(bool)u.IsDownloaded)
            {
                progress("Descargando…");
                dynamic downloader = Session.CreateUpdateDownloader();
                downloader.Updates = coll;
                dynamic dl = downloader.Download();
                int dlCode = dl.ResultCode;
                if (dlCode != ResultSucceeded && dlCode != ResultSucceededWithErrors)
                    return new InstallOutcome(false, false, $"Falló la descarga (0x{(int)dl.HResult:X8})");
            }

            progress("Instalando…");
            dynamic installer = Session.CreateUpdateInstaller();
            installer.Updates = coll;
            dynamic res = installer.Install();
            int code = res.ResultCode;
            bool reboot = res.RebootRequired;
            Log.Write($"Windows Update: ResultCode={code} reinicio={reboot}");
            if (code == ResultSucceeded || code == ResultSucceededWithErrors)
                return new InstallOutcome(true, reboot, null);

            int hr = 0;
            try { hr = res.GetUpdateResult(0).HResult; } catch { }
            return new InstallOutcome(false, reboot, $"Windows rechazó la instalación (0x{hr:X8})");
        }
        catch (Exception ex)
        {
            return new InstallOutcome(false, false, ex.Message);
        }
    }

    static string Str(Func<object?> get)
    {
        try { return get()?.ToString() ?? ""; } catch { return ""; }
    }
}
