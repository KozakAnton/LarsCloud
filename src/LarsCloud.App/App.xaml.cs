using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using LarsCloud.Infrastructure;
using LarsCloud.Models;
using LarsCloud.Services;
using LarsCloud.ViewModels;

namespace LarsCloud;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _instance;
    private HttpClient? _httpClient;
    private SyncScheduler? _scheduler;
    private SyncEngine? _engine;
    private TrayService? _tray;
    private MainWindow? _window;
    private LogService? _log;
    private bool _exiting;

    public App() => DispatcherUnhandledException += OnDispatcherUnhandledException;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _instance = new SingleInstanceGuard();
            if (!_instance.IsFirstInstance)
            {
                MessageBox.Show("Lar’s Cloud уже запущений. Знайдіть іконку програми біля годинника.",
                    "Lar’s Cloud", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            AppPaths.EnsureCreated();
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(15)
            };
            _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
            var log = new LogService();
            _log = log;
            await log.InfoAsync("Lar's Cloud starting.");
            var configuration = await ProductConfiguration.LoadAsync();
            var settings = new SettingsService();
            await settings.LoadAsync();
            var database = new StateDatabase();
            await database.InitializeAsync();
            var vault = new TokenVault();
            var oauth = new GoogleOAuthService(configuration, vault, _httpClient, log);
            await oauth.RestoreSessionAsync();
            var connectivity = new ConnectivityService(_httpClient);
            var drive = new GoogleDriveService(oauth, _httpClient, log);
            var scanner = new FileScanner(database);
            var engine = new SyncEngine(settings, connectivity, oauth, drive, scanner, database, log);
            _engine = engine;
            var updates = new UpdateService(configuration, _httpClient, log);
            var autostart = new AutostartService();
            autostart.SetEnabled(settings.Current.AutoStart);
            var viewModel = new MainViewModel(settings, oauth, drive, connectivity, engine, database, updates, autostart, log, configuration);
            _window = new MainWindow(viewModel);
            MainWindow = _window;

            _tray = new TrayService(
                () => Dispatcher.Invoke(_window.ShowFromTray),
                () => Dispatcher.Invoke(() => viewModel.SyncNowCommand.Execute(null)),
                () => Dispatcher.Invoke(() => viewModel.OpenLocalFolderCommand.Execute(null)),
                () => Dispatcher.Invoke(() => viewModel.OpenDriveCommand.Execute(null)),
                () => Dispatcher.Invoke(() => viewModel.TogglePauseCommand.Execute(null)),
                () => Dispatcher.Invoke(ExitApplication),
                settings.Current.SyncPaused);
            viewModel.NotificationRequested += (_, notification) => _tray?.Notify(notification.Title, notification.Message, notification.IsError);
            viewModel.PauseStateChanged += (_, _) => _tray?.SetPaused(settings.Current.SyncPaused);
            viewModel.UpdateInstallerReady += (_, path) => Dispatcher.Invoke(() => ExitApplication(path));

            var background = e.Args.Any(x => x.Equals("--background", StringComparison.OrdinalIgnoreCase));
            _window.Show();
            if (background && oauth.IsAuthenticated && settings.Current.StartMinimized) _window.Hide();
            await viewModel.InitializeAsync();

            _scheduler = new SyncScheduler(settings, oauth, engine, log);
            _scheduler.Start();
            _ = viewModel.CheckUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            _window?.AllowClose();
            _tray?.Dispose();
            MessageBox.Show($"Lar’s Cloud не вдалося запустити.\n\n{ex.Message}", "Помилка запуску",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void ExitApplication() => await ExitApplicationAsync(null);
    private async void ExitApplication(string installerPath) => await ExitApplicationAsync(installerPath);

    private async Task ExitApplicationAsync(string? installerPath)
    {
        if (_exiting) return;
        _exiting = true;
        var isInstallingUpdate = !string.IsNullOrWhiteSpace(installerPath);
        if (isInstallingUpdate)
        {
            try
            {
                var applicationPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LarsCloud.exe");
                UpdateService.LaunchInstallerAfterExit(installerPath!, Environment.ProcessId, applicationPath);
            }
            catch (Exception ex)
            {
                _exiting = false;
                MessageBox.Show($"Не вдалося підготувати Installer до запуску.\n\n{ex.Message}", "Оновлення Lar’s Cloud",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        _engine?.CancelCurrent();
        _window?.AllowClose();
        _tray?.Dispose();
        _tray = null;
        if (_scheduler is not null)
        {
            try
            {
                var disposeTask = _scheduler.DisposeAsync().AsTask();
                var completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
                if (completed == disposeTask) await disposeTask;
                else if (_log is not null) _ = _log.InfoAsync("Scheduler shutdown timed out during application exit.");
            }
            catch (Exception ex)
            {
                if (_log is not null) _ = _log.ErrorAsync("Scheduler shutdown failed", ex);
            }
            _scheduler = null;
        }
        _window?.Close();
        Shutdown();
        if (isInstallingUpdate) Environment.Exit(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _httpClient?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_log is not null) await _log.ErrorAsync("Unhandled UI error", e.Exception);
        MessageBox.Show("Сталася неочікувана помилка. Програма продовжить роботу; деталі збережено в журналі.",
            "Lar’s Cloud", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
