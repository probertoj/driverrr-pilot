using System.IO;
using System.Text.Json;
using System.Windows;

namespace Driverrr;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Para equipos donde la ventana sale en blanco por un problema del driver gráfico:
        //   Driverrr.exe --sin-aceleracion
        if (e.Args.Contains("--sin-aceleracion"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // Modo sin ventana:  Driverrr.exe --scan salida.json   (solo lee, no instala nada)
        int i = Array.IndexOf(e.Args, "--scan");
        if (i >= 0 && i + 1 < e.Args.Length)
        {
            Shutdown(RunHeadlessScan(e.Args[i + 1]));
            return;
        }

        // Actualización de la app instalada: se llega aquí ya elevado, tras aceptar el aviso en la ventana.
        if (e.Args.Contains("--self-update"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var text = new System.Windows.Controls.TextBlock { Text = "Preparando la actualización…", Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(text);
            panel.Children.Add(new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 6 });
            var progress = new Window
            {
                Title = "Driverrr", Width = 400, SizeToContent = SizeToContent.Height, Content = panel,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize
            };
            progress.Show();
            var updateError = await Updater.SelfUpdateAsync(msg => Dispatcher.Invoke(() => text.Text = msg));
            progress.Close();
            if (updateError != null)
                MessageBox.Show("No se pudo actualizar Driverrr:\n\n" + updateError + "\n\nNo se ha cambiado nada.",
                    "Driverrr", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(updateError == null ? 0 : 1);   // al cerrarse, el instalador de la versión nueva sustituye a esta
            return;
        }

        // Instalación y desinstalación: se llega aquí ya elevado, tras confirmar en la ventana o desde Windows.
        if (e.Args.Contains("--install"))
        {
            // Si lo lanza el actualizador, espera a que la versión anterior se cierre para poder sustituirla.
            int w = Array.IndexOf(e.Args, "--wait-pid");
            bool isUpdate = w >= 0;
            if (isUpdate && w + 1 < e.Args.Length && int.TryParse(e.Args[w + 1], out var pid))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(30_000); } catch { }   // ya había terminado
            }

            var error = Installer.Install();
            MessageBox.Show(error == null
                    ? (isUpdate
                        ? $"Driverrr se ha actualizado a la versión {Installer.ThisVersion}.\n\nÁbrelo desde el menú Inicio."
                        : "Driverrr se ha instalado.\n\nÁbrelo desde el menú Inicio (busca «Driverrr»). El fichero que descargaste ya no hace falta para usarlo.")
                    : "No se pudo instalar Driverrr:\n\n" + error,
                "Driverrr", MessageBoxButton.OK, error == null ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
                "¿Desinstalar Driverrr?\n\nSe conservan las copias de seguridad de drivers en C:\\ProgramData\\Driverrr.",
                "Driverrr", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes)
            {
                var error = Installer.Uninstall();
                MessageBox.Show(error == null ? "Driverrr se ha desinstalado." : "No se pudo desinstalar:\n\n" + error,
                    "Driverrr", MessageBoxButton.OK, error == null ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
