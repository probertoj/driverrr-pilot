using System.IO;
using System.Text.Json;
using System.Windows;

namespace DriverPilot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Para equipos donde la ventana sale en blanco por un problema del driver gráfico:
        //   DriverPilot.exe --sin-aceleracion
        if (e.Args.Contains("--sin-aceleracion"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Modo sin ventana:  DriverPilot.exe --scan salida.json   (solo lee, no instala nada)
        int i = Array.IndexOf(e.Args, "--scan");
        if (i >= 0 && i + 1 < e.Args.Length)
        {
            Shutdown(RunHeadlessScan(e.Args[i + 1]));
            return;
        }

        // Instalación y desinstalación: se llega aquí ya elevado, tras confirmar en la ventana o desde Windows.
        if (e.Args.Contains("--install"))
        {
            var error = Installer.Install();
            MessageBox.Show(error == null
                    ? "DriverPilot se ha instalado.\n\nÁbrelo desde el menú Inicio (busca «DriverPilot»). El fichero que descargaste ya no hace falta para usarlo."
                    : "No se pudo instalar DriverPilot:\n\n" + error,
                "DriverPilot", MessageBoxButton.OK, error == null ? MessageBoxImage.Information : MessageBoxImage.Warning);
            Shutdown(error == null ? 0 : 1);
            return;
        }
        if (e.Args.Contains("--uninstall"))
        {
            // Windows lanza la desinstalación sin elevar: se pide el permiso aquí.
            if (!SafetyService.IsAdmin())
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                    {
                        UseShellExecute = true, Verb = "runas", Arguments = "--uninstall"
                    });
                }
                catch (System.ComponentModel.Win32Exception) { }   // el usuario canceló el aviso
                Shutdown();
                return;
            }
            var ask = MessageBox.Show(
                "¿Desinstalar DriverPilot?\n\nSe conservan las copias de seguridad de drivers en C:\\ProgramData\\DriverPilot.",
                "DriverPilot", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes)
            {
                var error = Installer.Uninstall();
                MessageBox.Show(error == null ? "DriverPilot se ha desinstalado." : "No se pudo desinstalar:\n\n" + error,
                    "DriverPilot", MessageBoxButton.OK, error == null ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        new MainWindow(autoScan: e.Args.Contains("--autoscan"), openRestore: e.Args.Contains("--restore")).Show();
    }

    static int RunHeadlessScan(string outputPath)
    {
        try
        {
            var devices = DeviceScanner.Scan();
            string? wuError = null;
            try { DeviceScanner.AttachUpdates(devices, new WindowsUpdateService().Search()); }
            catch (Exception ex) { wuError = ex.Message; }
            try { Task.Run(() => CatalogService.FindUpdatesAsync(devices)).GetAwaiter().GetResult(); }
            catch (Exception ex) { wuError ??= ex.Message; }

            int instances = devices.Count;
            devices = DeviceScanner.Collapse(devices);

            var report = new
            {
                scannedAt = DateTime.Now,
                totalDevices = instances,
                updates = devices.Count(d => d.Update != null),
                problems = devices.Count(d => d.State == DeviceState.Problem),
                windowsUpdateError = wuError,
                catalogFailedQueries = CatalogService.FailedQueries,
                attention = devices.Where(d => d.NeedsAttention),
                all = devices
            };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(outputPath, "ERROR: " + ex); } catch { }
            return 1;
        }
    }
}
