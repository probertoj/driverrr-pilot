using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;

namespace Driverrr;

public partial class MainWindow : Window
{
    readonly ObservableCollection<DeviceItem> _devices = new();
    readonly ListCollectionView _updatesView, _allView;
    readonly WindowsUpdateService _wu = new();
    bool _busy, _scanned, _incomplete;
    int _instances;

    public MainWindow(bool autoScan = false, bool openRestore = false)
    {
        Loaded += (_, _) => { if (openRestore) NavRestore.IsChecked = true; };
        InitializeComponent();

        _updatesView = new ListCollectionView(_devices) { Filter = o => ((DeviceItem)o).NeedsAttention };
        _allView = new ListCollectionView(_devices);
        foreach (var v in new[] { _updatesView, _allView })
            v.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DeviceItem.Category)));
        UpdatesList.ItemsSource = _updatesView;
        DevicesList.ItemsSource = _allView;

        AdminText.Text = SafetyService.IsAdmin()
            ? "Ejecutándose como administrador"
            : "Sin permisos de administrador: se pedirán al instalar";

        // Fuera de «Archivos de programa» la app es modificable sin permisos: se ofrece instalarla.
        if (!Installer.RunningInstalled)
        {
            InstallBox.Visibility = Visibility.Visible;
            var installed = Installer.InstalledVersion;
            InstallHint.Text = installed == null
                ? "Esta copia está en una carpeta que cualquier programa puede modificar."
                : $"Ya hay una copia instalada (versión {installed}). Esta es la {Installer.ThisVersion}.";
            InstallLink.Inlines.Clear();
            InstallLink.Inlines.Add(installed == null ? "Instalar en este equipo" : "Reinstalar con esta copia");
        }

        Loaded += async (_, _) =>
        {
            MachineText.Text = await Task.Run(DeviceScanner.MachineSummary);
            if (autoScan) await ScanAsync();
        };
    }

    // ---------- Navegación ----------

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage == null) return;   // durante InitializeComponent
        HomePage.Visibility = Vis(NavHome.IsChecked == true);
        UpdatesPage.Visibility = Vis(NavUpdates.IsChecked == true);
        DevicesPage.Visibility = Vis(NavDevices.IsChecked == true);
        RestorePage.Visibility = Vis(NavRestore.IsChecked == true);
        if (NavRestore.IsChecked == true) _ = LoadRestoreListAsync();
    }

    // ---------- Restaurar ----------

    async Task LoadRestoreListAsync()
    {
        try
        {
            var items = await Task.Run(RestoreService.List);
            RestoreList.ItemsSource = items;
            RestoreEmpty.Visibility = Vis(items.Count == 0);
            RestoreEmpty.Text = "No hay nada que restaurar.\nAquí aparecerán los drivers que actualices con Driverrr.";
        }
        catch (Exception ex)
        {
            RestoreList.ItemsSource = null;
            RestoreEmpty.Visibility = Visibility.Visible;
            RestoreEmpty.Text = "No se pudieron leer las copias de seguridad: " + ex.Message;
        }
    }

    async void RestoreOne_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || ((FrameworkElement)sender).DataContext is not RestoreItem item) return;

        if (!SafetyService.IsAdmin())
        {
            var ask = MessageBox.Show(this,
                "Restaurar un driver requiere permisos de administrador.\n\n¿Reiniciar Driverrr como administrador? Se abrirá de nuevo en esta página.",
                "Driverrr", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes) RelaunchElevated("--restore");
            return;
        }

        int sharing = await Task.Run(() => RestoreService.DevicesUsing(item.CurrentInf));
        var confirm = MessageBox.Show(this,
            $"Se va a restaurar el driver de:\n\n  • {item.DeviceName}\n\nDe la versión {item.CurrentVersion} a la {item.OldVersion}.\n\n" +
            (sharing > 1 ? $"Ese driver lo comparten hasta {sharing} dispositivos: todos volverán al anterior.\n\n" : "") +
            "Se retirará el driver actual para que Windows recupere el anterior. El dispositivo puede dejar de funcionar unos segundos. Antes se creará un punto de restauración. ¿Continuar?",
            "Confirmar restauración", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        SetBusy(true, "Creando punto de restauración…");
        try
        {
            Log.Write($"=== Restauración, admin={SafetyService.IsAdmin()} ===");
            var rpError = await Task.Run(() => SafetyService.CreateRestorePoint("Driverrr: antes de restaurar un driver"));
            Log.Write("Punto de restauración: " + (rpError ?? "creado"));
            if (rpError != null)
            {
                var go = MessageBox.Show(this,
                    $"No se pudo crear el punto de restauración: {rpError}\n\n¿Continuar de todos modos?",
                    "Driverrr", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (go != MessageBoxResult.Yes) return;
            }

            StatusText.Text = $"Restaurando: {item.DeviceName}";
            var outcome = await Task.Run(() => RestoreService.Restore(item, msg =>
            {
                Log.Write("Paso: " + msg);
                Dispatcher.Invoke(() => item.Message = msg);
            }));
            Log.Write($"Restauración: ok={outcome.Success} reinicio={outcome.RebootRequired} nota={outcome.Error ?? "-"}");

            item.Done = outcome.Success;
            item.Message = outcome.Success
                ? (outcome.Error ?? (outcome.RebootRequired ? "Restaurado (reinicio pendiente)" : $"Restaurado a {item.OldVersion}"))
                : outcome.Error;
            StatusText.Text = outcome.Success ? "Restauración terminada. Vuelve a escanear para ver el estado actual." : "No se pudo restaurar.";
            if (outcome.RebootRequired)
                MessageBox.Show(this, "Reinicia el equipo para terminar de restaurar el driver.", "Driverrr",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            SetBusy(false);
        }
    }

    static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ---------- Escaneo ----------

    async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    async Task ScanAsync()
    {
        if (_busy) return;
        NavHome.IsChecked = true;
        SetBusy(true, "Leyendo los dispositivos del equipo…");
        HomeTitle.Text = "Analizando tu equipo…";
        try
        {
            var devices = await Task.Run(DeviceScanner.Scan);

            StatusText.Text = "Buscando drivers en Windows Update (puede tardar un minuto)…";
            string? wuError = null;
            try
            {
                var updates = await Task.Run(_wu.Search);
                DeviceScanner.AttachUpdates(devices, updates);
            }
            catch (Exception ex)
            {
                wuError = ex.Message;
            }

            StatusText.Text = "Consultando el Catálogo de Microsoft Update…";
            try
            {
                await CatalogService.FindUpdatesAsync(devices, msg => Dispatcher.Invoke(() => StatusText.Text = msg));
            }
            catch (Exception ex)
            {
                wuError ??= ex.Message;
            }

            if (CatalogService.FailedQueries > 0)
                wuError ??= $"el Catálogo no respondió a {CatalogService.FailedQueries} consulta(s); vuelve a escanear en unos minutos";

            int instances = devices.Count;
            devices = DeviceScanner.Collapse(devices);
            _instances = instances;

            _devices.Clear();
            foreach (var d in devices.OrderBy(d => d.Category).ThenBy(d => d.Name))
                _devices.Add(d);

            _scanned = true;
            LastScanText.Text = $"Último escaneo: hoy a las {DateTime.Now:HH:mm}";
            StatusText.Text = wuError == null
                ? "Escaneo terminado. Fuentes: Windows Update y Catálogo de Microsoft Update."
                : "Escaneo incompleto, una fuente falló: " + wuError;
            // Un escaneo incompleto no debe pasar desapercibido en el pie de la ventana.
            _incomplete = wuError != null;
            if (wuError != null)
            {
                StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
                StatusText.FontWeight = FontWeights.SemiBold;
            }
            NavUpdates.IsChecked = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Error al escanear: " + ex.Message;
        }
        finally
        {
            HomeTitle.Text = "Comprueba los drivers de tu equipo";
            SetBusy(false);
            RefreshSummary();
        }
    }

    // ---------- Instalación ----------

    async void UpdateOne_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is DeviceItem d) await InstallAsync(new List<DeviceItem> { d });
    }

    async void UpdateAll_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync(_devices.Where(d => d.CanUpdate).ToList());

    async Task InstallAsync(List<DeviceItem> items)
    {
        if (_busy || items.Count == 0) return;

        if (!SafetyService.IsAdmin())
        {
            var ask = MessageBox.Show(this,
                "Instalar drivers requiere permisos de administrador.\n\n¿Reiniciar Driverrr como administrador? Se volverá a escanear automáticamente.",
                "Driverrr", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes) RelaunchElevated();
            return;
        }

        var names = string.Join("\n", items.Take(8).Select(d => "  • " + d.Name)) + (items.Count > 8 ? "\n  …" : "");
        var confirm = MessageBox.Show(this,
            $"Se van a actualizar {items.Count} driver(s):\n\n{names}\n\nAntes se creará un punto de restauración y una copia de los drivers actuales. ¿Continuar?",
            "Confirmar actualización", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.OK) return;

        SetBusy(true, "Creando punto de restauración…");
        try
        {
            Log.Write($"=== Instalación: {items.Count} fila(s), admin={SafetyService.IsAdmin()} ===");
            var rpError = await Task.Run(() => SafetyService.CreateRestorePoint("Driverrr: antes de actualizar drivers"));
            Log.Write("Punto de restauración: " + (rpError ?? "creado"));
            if (rpError != null)
            {
                var go = MessageBox.Show(this,
                    $"No se pudo crear el punto de restauración: {rpError}\n\n¿Continuar de todos modos?",
                    "Driverrr", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (go != MessageBoxResult.Yes) return;
            }

            // Carpeta protegida: solo administradores pueden escribir en ella.
            string backupFolder;
            try
            {
                backupFolder = SecurePaths.MachineDir("Backups", DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss"));
            }
            catch (Exception ex)
            {
                Log.Write("Carpeta de copias rechazada: " + ex.Message);
                MessageBox.Show(this, "No se puede preparar la carpeta de copias de seguridad:\n\n" + ex.Message +
                                      "\n\nNo se ha instalado nada.", "Driverrr", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int ok = 0, failed = 0;
            bool reboot = false;

            // Varios dispositivos pueden compartir un mismo paquete: se instala una sola vez.
            foreach (var group in items.GroupBy(d => d.Update!.UpdateId))
            {
                var update = group.First().Update!;
                foreach (var d in group) { d.State = DeviceState.Working; d.Message = "Guardando copia…"; }
                StatusText.Text = $"Actualizando: {group.First().Name}";

                var first = group.First();
                Log.Write($"--- {first.DisplayName} | {first.Version} -> {first.NewVersion} | fuente={update.Source} | id={update.UpdateId} | inf={first.InfName} | dev={first.DeviceId}");

                var backup = await Task.Run(() => SafetyService.BackupDriver(first, backupFolder));
                Log.Write($"Copia de seguridad: {backup.Result} {backup.Folder}");
                if (backup.Result == BackupResult.Failed)
                {
                    // Sin copia del driver actual no se toca nada: no habría forma sencilla de volver atrás.
                    foreach (var d in group)
                    {
                        d.State = DeviceState.Failed;
                        d.Message = "No se pudo guardar copia del driver actual; no se ha instalado.";
                    }
                    failed++;
                    continue;
                }

                void Report(string msg)
                {
                    Log.Write("Paso: " + msg);
                    Dispatcher.Invoke(() => { foreach (var d in group) d.Message = msg; });
                }
                var outcome = update.Source == UpdateSource.Catalog
                    ? await Task.Run(() => CatalogService.InstallAsync(group.First(), Report))
                    : await Task.Run(() => _wu.Install(update, Report));

                var after = await Task.Run(() => DeviceScanner.InstalledVersion(first.DeviceId));
                Log.Write($"Resultado: ok={outcome.Success} reinicio={outcome.RebootRequired} error={outcome.Error ?? "-"} | versión ahora={after ?? "?"}");

                reboot |= outcome.RebootRequired;
                foreach (var d in group)
                {
                    d.State = outcome.Success ? DeviceState.Installed : DeviceState.Failed;
                    d.Message = outcome.Success
                        ? (outcome.RebootRequired ? "Actualizado (reinicio pendiente)" : null)
                        : outcome.Error;
                }
                if (outcome.Success) ok++; else failed++;
            }

            StatusText.Text = $"Terminado: {ok} actualizado(s)" + (failed > 0 ? $", {failed} con error" : "") + "."
                              + (Directory.EnumerateFileSystemEntries(backupFolder).Any() ? $"  Copia de seguridad en {backupFolder}" : "");
            try { if (!Directory.EnumerateFileSystemEntries(backupFolder).Any()) Directory.Delete(backupFolder); } catch { }
            if (reboot)
                MessageBox.Show(this, "Reinicia el equipo para terminar de aplicar los drivers.", "Driverrr",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            SetBusy(false);
            RefreshSummary();
        }
    }

    void RelaunchElevated(string argument = "--autoscan")
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = argument
            });
            Application.Current.Shutdown();
        }
        catch (Win32Exception)
        {
            StatusText.Text = "Se canceló la elevación de permisos.";
        }
    }

    // ---------- Interfaz ----------

    void Vendor_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkContentElement)sender).DataContext is DeviceItem { VendorUrl: { } url })
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var ask = MessageBox.Show(this,
            $"Se va a instalar Driverrr en:\n\n  {Installer.InstallDir}\n\n" +
            "Ahí solo un administrador puede modificarlo. Se creará un acceso directo en el menú Inicio y podrás desinstalarlo desde Configuración > Aplicaciones.\n\n" +
            "Windows pedirá permisos de administrador y esta ventana se cerrará. ¿Continuar?",
            "Instalar Driverrr", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ask == MessageBoxResult.Yes) RelaunchElevated("--install");
    }

    void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(SafetyService.BackupRoot))
            Process.Start(new ProcessStartInfo(SafetyService.BackupRoot) { UseShellExecute = true });
        else
            StatusText.Text = "Todavía no hay copias de seguridad: se crean al actualizar un driver.";
    }

    void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (busy)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Muted");
            StatusText.FontWeight = FontWeights.Normal;
        }
        ScanButton.IsEnabled = !busy;
        ScanButton.Content = busy ? "Escaneando…" : "Escanear";
        UpdateAllButton.IsEnabled = !busy && _devices.Any(d => d.CanUpdate);
        Progress.Visibility = Vis(busy);
        if (status != null) StatusText.Text = status;
    }

    void RefreshSummary()
    {
        _updatesView.Refresh();
        _allView.Refresh();

        int updates = _devices.Count(d => d.CanUpdate);
        int problems = _devices.Count(d => d.State == DeviceState.Problem);

        UpdateAllButton.IsEnabled = !_busy && updates > 0;
        UpdateAllButton.Content = updates > 0 ? $"Actualizar todo ({updates})" : "Actualizar todo";
        UpdatesBadge.Visibility = Vis(updates > 0);
        UpdatesBadgeText.Text = updates.ToString();

        if (!_scanned) return;

        UpdatesTitle.Text = updates switch
        {
            // Sin haber podido consultar todas las fuentes no se puede afirmar que todo esté al día.
            0 => problems > 0 ? "Hay dispositivos con problemas" : _incomplete ? "Escaneo incompleto" : "Todo al día",
            1 => "1 driver por actualizar",
            _ => $"{updates} drivers por actualizar"
        };
        UpdatesSubtitle.Text = $"{_instances} dispositivos analizados"
                               + (problems > 0 ? $"  ·  {problems} con problemas" : "");
        DevicesSubtitle.Text = $"{_instances} dispositivos, {_devices.Count} drivers distintos";

        bool empty = _updatesView.IsEmpty;
        UpdatesEmpty.Visibility = Vis(empty);
        UpdatesEmptyIcon.Visibility = Vis(!_incomplete);
        UpdatesEmptyText.Text = _incomplete
            ? "No se pudieron consultar todas las fuentes, así que puede haber actualizaciones que no se ven.\nVuelve a escanear en unos minutos."
            : "No hay drivers pendientes ni dispositivos con problemas.";
    }
}
