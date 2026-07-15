using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LarsCloud.Infrastructure;
using LarsCloud.Models;
using LarsCloud.Services;
using Forms = System.Windows.Forms;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace LarsCloud.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly GoogleOAuthService _oauth;
    private readonly GoogleDriveService _drive;
    private readonly ConnectivityService _connectivity;
    private readonly SyncEngine _sync;
    private readonly StateDatabase _database;
    private readonly UpdateService _updates;
    private readonly AutostartService _autostart;
    private readonly LogService _log;
    private readonly ProductConfiguration _configuration;
    private bool _isAuthenticated;
    private int _selectedPage;
    private bool _isOnline;
    private bool _isDriveAvailable;
    private bool _isSyncing;
    private string _accountDisplay = "Google Drive не підключено";
    private string _statusText = "Перевірка стану…";
    private MediaBrush _statusBrush = new SolidColorBrush(MediaColor.FromRgb(89, 108, 154));
    private string _localFolder = "Папку ще не вибрано";
    private string _lastSync = "Ще не виконувалась";
    private string _nextSync = "Не заплановано";
    private string _driveUsage = "Немає даних";
    private string _driveRemaining = "Підключіть Google Drive";
    private double _driveUsagePercent;
    private MediaBrush _driveUsageBrush = new SolidColorBrush(MediaColor.FromRgb(46, 93, 252));
    private string _quotaWarning = "";
    private string _localFolderSize = "—";
    private string _localFileCount = "—";
    private string _changedFileCount = "—";
    private string _pendingUploadSize = "—";
    private double _syncPercent;
    private string _syncProgressText = "Очікування";
    private string _currentFile = "";
    private string _speedText = "";
    private string _etaText = "";
    private UpdateInfo? _availableUpdate;
    private bool _isUpdatePromptVisible;

    public MainViewModel(SettingsService settings, GoogleOAuthService oauth, GoogleDriveService drive, ConnectivityService connectivity,
        SyncEngine sync, StateDatabase database, UpdateService updates, AutostartService autostart,
        LogService log, ProductConfiguration configuration)
    {
        _settings = settings;
        _oauth = oauth;
        _drive = drive;
        _connectivity = connectivity;
        _sync = sync;
        _database = database;
        _updates = updates;
        _autostart = autostart;
        _log = log;
        _configuration = configuration;

        SignInCommand = new AsyncCommand(SignInAsync, () => !IsSyncing);
        SignOutCommand = new AsyncCommand(SignOutAsync, () => !IsSyncing);
        SelectFolderCommand = new AsyncCommand(SelectFolderAsync, () => !IsSyncing);
        CreateDesktopFolderCommand = new AsyncCommand(CreateDesktopFolderAsync, () => !IsSyncing);
        AnalyzeFolderCommand = new AsyncCommand(AnalyzeFolderAsync, () => Directory.Exists(_settings.Current.LocalFolder));
        SyncNowCommand = new AsyncCommand(SyncNowAsync, () => !IsSyncing);
        CancelSyncCommand = new RelayCommand(() => _sync.CancelCurrent(), () => IsSyncing);
        OpenLocalFolderCommand = new RelayCommand(OpenLocalFolder);
        OpenDriveCommand = new RelayCommand(OpenDriveFolder);
        ShowDashboardCommand = new RelayCommand(() => SelectedPage = 0);
        ShowHistoryCommand = new RelayCommand(() => SelectedPage = 1);
        ShowSettingsCommand = new RelayCommand(() => SelectedPage = 2);
        CheckUpdatesCommand = new AsyncCommand(() => CheckUpdatesAsync(false));
        InstallUpdateCommand = new AsyncCommand(InstallUpdateAsync, () => AvailableUpdate is not null);
        DismissUpdateCommand = new RelayCommand(() => IsUpdatePromptVisible = false);
        OpenLogsCommand = new RelayCommand(() => ProcessLauncher.Open(AppPaths.LogsDirectory));
        ClearLogsCommand = new AsyncCommand(ClearLogsAsync);
        ClearServiceDataCommand = new AsyncCommand(ClearServiceDataAsync, () => !IsSyncing);
        OpenPrivacyCommand = new RelayCommand(OpenPrivacy);
        TogglePauseCommand = new AsyncCommand(TogglePauseAsync);

        _sync.ProgressChanged += SyncOnProgressChanged;
        _sync.Completed += SyncOnCompleted;
        _sync.AnalysisCompleted += SyncOnAnalysisCompleted;
        _oauth.SessionChanged += (_, _) => OnUi(RefreshIdentity);
    }

    public event EventHandler<UserNotification>? NotificationRequested;
    public event EventHandler<string>? UpdateInstallerReady;
    public event EventHandler? PauseStateChanged;

    public ICommand SignInCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand CreateDesktopFolderCommand { get; }
    public ICommand AnalyzeFolderCommand { get; }
    public ICommand SyncNowCommand { get; }
    public ICommand CancelSyncCommand { get; }
    public ICommand OpenLocalFolderCommand { get; }
    public ICommand OpenDriveCommand { get; }
    public ICommand ShowDashboardCommand { get; }
    public ICommand ShowHistoryCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand InstallUpdateCommand { get; }
    public ICommand DismissUpdateCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand ClearLogsCommand { get; }
    public ICommand ClearServiceDataCommand { get; }
    public ICommand OpenPrivacyCommand { get; }
    public ICommand TogglePauseCommand { get; }

    public IReadOnlyList<int> SyncIntervals { get; } = Enumerable.Range(1, 7).ToArray();
    public ObservableCollection<HistoryRow> History { get; } = new();

    public bool IsAuthenticated { get => _isAuthenticated; private set { if (SetProperty(ref _isAuthenticated, value)) OnPropertyChanged(nameof(IsNotAuthenticated)); } }
    public bool IsNotAuthenticated => !IsAuthenticated;
    private int SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (!SetProperty(ref _selectedPage, value)) return;
            OnPropertyChanged(nameof(IsDashboardPage));
            OnPropertyChanged(nameof(IsHistoryPage));
            OnPropertyChanged(nameof(IsSettingsPage));
        }
    }
    public bool IsDashboardPage => SelectedPage == 0;
    public bool IsHistoryPage => SelectedPage == 1;
    public bool IsSettingsPage => SelectedPage == 2;
    public bool IsOnline { get => _isOnline; private set => SetProperty(ref _isOnline, value); }
    public bool IsDriveAvailable { get => _isDriveAvailable; private set => SetProperty(ref _isDriveAvailable, value); }
    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (!SetProperty(ref _isSyncing, value)) return;
            (SyncNowCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (CancelSyncCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SignInCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (SignOutCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (SelectFolderCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (CreateDesktopFolderCommand as AsyncCommand)?.RaiseCanExecuteChanged();
            (ClearServiceDataCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string AccountDisplay { get => _accountDisplay; private set => SetProperty(ref _accountDisplay, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public MediaBrush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }
    public string LocalFolder { get => _localFolder; private set => SetProperty(ref _localFolder, value); }
    public string LastSync { get => _lastSync; private set => SetProperty(ref _lastSync, value); }
    public string NextSync { get => _nextSync; private set => SetProperty(ref _nextSync, value); }
    public string DriveUsage { get => _driveUsage; private set => SetProperty(ref _driveUsage, value); }
    public string DriveRemaining { get => _driveRemaining; private set => SetProperty(ref _driveRemaining, value); }
    public double DriveUsagePercent { get => _driveUsagePercent; private set => SetProperty(ref _driveUsagePercent, value); }
    public MediaBrush DriveUsageBrush { get => _driveUsageBrush; private set => SetProperty(ref _driveUsageBrush, value); }
    public string QuotaWarning { get => _quotaWarning; private set => SetProperty(ref _quotaWarning, value); }
    public string LocalFolderSize { get => _localFolderSize; private set => SetProperty(ref _localFolderSize, value); }
    public string LocalFileCount { get => _localFileCount; private set => SetProperty(ref _localFileCount, value); }
    public string ChangedFileCount { get => _changedFileCount; private set => SetProperty(ref _changedFileCount, value); }
    public string PendingUploadSize { get => _pendingUploadSize; private set => SetProperty(ref _pendingUploadSize, value); }
    public double SyncPercent { get => _syncPercent; private set => SetProperty(ref _syncPercent, value); }
    public string SyncProgressText { get => _syncProgressText; private set => SetProperty(ref _syncProgressText, value); }
    public string CurrentFile { get => _currentFile; private set => SetProperty(ref _currentFile, value); }
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => SetProperty(ref _etaText, value); }
    public string CurrentVersion => $"Версія {_updates.CurrentVersion.ToString(3)}";
    public string UpdateText => AvailableUpdate is null ? "Оновлень не знайдено" : $"Доступна версія {AvailableUpdate.Version}";
    public bool IsUpdatePromptVisible
    {
        get => _isUpdatePromptVisible;
        private set => SetProperty(ref _isUpdatePromptVisible, value);
    }

    public UpdateInfo? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (!SetProperty(ref _availableUpdate, value)) return;
            OnPropertyChanged(nameof(UpdateText));
            (InstallUpdateCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        }
    }

    public int SyncIntervalDays
    {
        get => _settings.Current.SyncIntervalDays;
        set
        {
            if (_settings.Current.SyncIntervalDays == value) return;
            _settings.Current.SyncIntervalDays = Math.Clamp(value, 1, 7);
            if (_settings.Current.LastSyncUtc is { } last) _settings.Current.NextSyncUtc = last.AddDays(_settings.Current.SyncIntervalDays);
            OnPropertyChanged();
            _ = SaveSettingsQuietlyAsync();
            RefreshDates();
        }
    }

    public bool AutoStart
    {
        get => _settings.Current.AutoStart;
        set
        {
            if (_settings.Current.AutoStart == value) return;
            _settings.Current.AutoStart = value;
            _autostart.SetEnabled(value);
            OnPropertyChanged();
            _ = SaveSettingsQuietlyAsync();
        }
    }

    public bool NotificationsEnabled
    {
        get => _settings.Current.NotificationsEnabled;
        set { if (_settings.Current.NotificationsEnabled != value) { _settings.Current.NotificationsEnabled = value; OnPropertyChanged(); _ = SaveSettingsQuietlyAsync(); } }
    }

    public bool ErrorNotificationsEnabled
    {
        get => _settings.Current.ErrorNotificationsEnabled;
        set { if (_settings.Current.ErrorNotificationsEnabled != value) { _settings.Current.ErrorNotificationsEnabled = value; OnPropertyChanged(); _ = SaveSettingsQuietlyAsync(); } }
    }

    public bool DeleteRemoteWhenLocalDeleted
    {
        get => _settings.Current.DeleteRemoteWhenLocalDeleted;
        set { if (_settings.Current.DeleteRemoteWhenLocalDeleted != value) { _settings.Current.DeleteRemoteWhenLocalDeleted = value; OnPropertyChanged(); _ = SaveSettingsQuietlyAsync(); } }
    }

    public bool SyncPaused => _settings.Current.SyncPaused;
    public string PauseButtonText => SyncPaused ? "Відновити синхронізацію" : "Призупинити синхронізацію";

    public async Task InitializeAsync()
    {
        RefreshIdentity();
        LocalFolder = string.IsNullOrWhiteSpace(_settings.Current.LocalFolder) ? "Папку ще не вибрано" : _settings.Current.LocalFolder;
        RefreshDates();
        OnPropertyChanged(nameof(SyncIntervalDays));
        OnPropertyChanged(nameof(AutoStart));
        OnPropertyChanged(nameof(NotificationsEnabled));
        OnPropertyChanged(nameof(ErrorNotificationsEnabled));
        OnPropertyChanged(nameof(DeleteRemoteWhenLocalDeleted));
        await RefreshHistoryAsync();
        await RefreshConnectionAsync();
        var automaticRunIsDue = _oauth.IsAuthenticated && !_settings.Current.SyncPaused
                                && (_settings.Current.NextSyncUtc ?? DateTimeOffset.UtcNow) <= DateTimeOffset.UtcNow;
        if (Directory.Exists(_settings.Current.LocalFolder) && !automaticRunIsDue)
            await AnalyzeFolderAsync(silent: true);
    }

    public async Task CheckUpdatesAsync(bool silent)
    {
        try
        {
            AvailableUpdate = await _updates.CheckAsync();
            if (AvailableUpdate is not null)
            {
                IsUpdatePromptVisible = true;
                NotificationRequested?.Invoke(this, new UserNotification("Доступне оновлення",
                    $"Lar’s Cloud {AvailableUpdate.Version} готовий до завантаження.", false));
            }
            else
            {
                IsUpdatePromptVisible = false;
                if (!silent) MessageBox.Show("У вас установлена найновіша версія.", "Lar’s Cloud",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            await _log.ErrorAsync("Update check failed", ex);
            if (!silent) MessageBox.Show("Не вдалося перевірити оновлення. GitHub тимчасово недоступний або не налаштований.",
                "Lar’s Cloud", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SignInAsync()
    {
        try
        {
            StatusText = "Очікування входу через браузер…";
            StatusBrush = Brush("#F6B84A");
            await _oauth.SignInAsync();
            await RefreshConnectionAsync();
        }
        catch (Exception ex) when (ex is OAuthException or AppConfigurationException or HttpRequestException)
        {
            StatusText = ex.Message;
            StatusBrush = Brush("#FF5573");
            MessageBox.Show(ex.Message, "Google Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SignOutAsync()
    {
        if (MessageBox.Show("Від’єднати поточний Google-акаунт? Локальні файли не буде змінено.", "Lar’s Cloud",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _oauth.SignOutAsync();
        IsDriveAvailable = false;
        DriveUsage = "Немає даних";
        DriveRemaining = "Підключіть Google Drive";
        DriveUsagePercent = 0;
    }

    private async Task SelectFolderAsync()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Виберіть папку для резервного копіювання",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_settings.Current.LocalFolder) ? _settings.Current.LocalFolder : ""
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        await SetFolderAsync(dialog.SelectedPath);
    }

    private async Task CreateDesktopFolderAsync()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var folder = Path.Combine(desktop, "Lar's Cloud");
        Directory.CreateDirectory(folder);
        await SetFolderAsync(folder);
        ProcessLauncher.Open(folder);
    }

    private async Task SetFolderAsync(string folder)
    {
        var normalized = Path.GetFullPath(folder);
        if (!string.Equals(_settings.Current.LocalFolder, normalized, StringComparison.OrdinalIgnoreCase))
            await _database.ClearFileStateAsync();
        _settings.Current.LocalFolder = normalized;
        _settings.Current.NextSyncUtc = DateTimeOffset.UtcNow;
        await _settings.SaveAsync();
        LocalFolder = _settings.Current.LocalFolder;
        RefreshDates();
        await AnalyzeFolderAsync();
    }

    private async Task AnalyzeFolderAsync() => await AnalyzeFolderAsync(silent: false);
    private async Task AnalyzeFolderAsync(bool silent)
    {
        try
        {
            SyncProgressText = "Підрахунок файлів…";
            var progress = new Progress<(int Files, long Bytes)>(x =>
            {
                LocalFileCount = x.Files.ToString();
                LocalFolderSize = Formatters.Bytes(x.Bytes);
            });
            var scan = await _sync.AnalyzeAsync(progress);
            LocalFileCount = scan.TotalFiles.ToString();
            LocalFolderSize = Formatters.Bytes(scan.TotalBytes);
            ChangedFileCount = scan.ChangedFiles.Count.ToString();
            PendingUploadSize = Formatters.Bytes(scan.UploadBytes);
            if (!IsSyncing) SyncProgressText = scan.ChangedFiles.Count == 0 ? "Усі локальні файли враховано" : "Є зміни для резервування";
        }
        catch (Exception ex)
        {
            LocalFileCount = LocalFolderSize = ChangedFileCount = PendingUploadSize = "—";
            if (!silent) MessageBox.Show(ex.Message, "Аналіз папки", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SyncNowAsync()
    {
        IsSyncing = true;
        try { await Task.Run(() => _sync.RunAsync(manual: true)); }
        finally { IsSyncing = false; }
        await RefreshConnectionAsync();
        await RefreshHistoryAsync();
        if (Directory.Exists(_settings.Current.LocalFolder)) await AnalyzeFolderAsync(silent: true);
    }

    private async Task RefreshConnectionAsync()
    {
        IsOnline = await _connectivity.IsOnlineAsync();
        if (!IsOnline)
        {
            IsDriveAvailable = false;
            StatusText = "Немає підключення до інтернету";
            StatusBrush = Brush("#FF5573");
            return;
        }
        if (!_oauth.IsAuthenticated)
        {
            IsDriveAvailable = false;
            StatusText = "Google Drive не підключений";
            StatusBrush = Brush("#FF5573");
            return;
        }
        try
        {
            var about = await _drive.GetAboutAsync();
            IsDriveAvailable = true;
            StatusText = _settings.Current.SyncPaused ? "Автоматичну синхронізацію призупинено" : "Захист файлів активний";
            StatusBrush = _settings.Current.SyncPaused ? Brush("#F6B84A") : Brush("#2ACE7A");
            DriveUsagePercent = about.Quota.UsedPercent;
            DriveUsageBrush = about.Quota.UsedPercent >= 95 ? Brush("#FF5573")
                : about.Quota.UsedPercent >= 80 ? Brush("#F6B84A") : Brush("#2E5DFC");
            DriveUsage = about.Quota.Limit is long limit
                ? $"Використано {Formatters.Bytes(about.Quota.Usage)} із {Formatters.Bytes(limit)}"
                : $"Використано {Formatters.Bytes(about.Quota.Usage)}";
            DriveRemaining = about.Quota.Remaining is long remaining ? $"Залишилося {Formatters.Bytes(remaining)}" : "Без фіксованого ліміту";
            QuotaWarning = about.Quota.UsedPercent >= 80 ? "Сховище заповнено більш ніж на 80%" : "";
        }
        catch (Exception ex)
        {
            IsDriveAvailable = false;
            DriveUsage = "Немає даних";
            DriveRemaining = "Перевірте доступ до Google Drive";
            DriveUsagePercent = 0;
            QuotaWarning = "";
            StatusText = ex switch
            {
                ReauthenticationRequiredException => ex.Message,
                DriveApiException => ex.Message,
                OAuthException => ex.Message,
                _ => "Google Drive тимчасово недоступний"
            };
            StatusBrush = Brush("#FF5573");
            await _log.ErrorAsync("Google Drive availability check failed", ex);
        }
    }

    private async Task RefreshHistoryAsync()
    {
        var items = await _database.GetRecentHistoryAsync(3);
        OnUi(() =>
        {
            History.Clear();
            foreach (var item in items)
                History.Add(new HistoryRow(Formatters.DateTimeLocal(item.FinishedUtc),
                    item.Status switch { SyncRunStatus.Success => "Успішно", SyncRunStatus.Cancelled => "Скасовано", _ => "Помилка" },
                    item.UploadedFiles, Formatters.Bytes(item.UploadedBytes), item.Error,
                    item.Status == SyncRunStatus.Success ? Brush("#2ACE7A") : item.Status == SyncRunStatus.Cancelled ? Brush("#F6B84A") : Brush("#FF5573")));
        });
    }

    private async Task TogglePauseAsync()
    {
        _settings.Current.SyncPaused = !_settings.Current.SyncPaused;
        if (_settings.Current.SyncPaused) _sync.CancelCurrent();
        else _settings.Current.NextSyncUtc ??= DateTimeOffset.UtcNow;
        await _settings.SaveAsync();
        OnPropertyChanged(nameof(SyncPaused));
        OnPropertyChanged(nameof(PauseButtonText));
        PauseStateChanged?.Invoke(this, EventArgs.Empty);
        await RefreshConnectionAsync();
    }

    private async Task InstallUpdateAsync()
    {
        var update = AvailableUpdate;
        if (update is null) return;
        IsUpdatePromptVisible = false;
        try
        {
            StatusText = "Завантаження оновлення…";
            StatusBrush = Brush("#F6B84A");
            var progress = new Progress<double>(value => StatusText = $"Завантаження оновлення: {value:0}%");
            var path = await _updates.DownloadAsync(update, progress);
            UpdateInstallerReady?.Invoke(this, path);
        }
        catch (Exception ex)
        {
            IsUpdatePromptVisible = true;
            await _log.ErrorAsync("Update installation failed", ex);
            MessageBox.Show(ex.Message, "Оновлення", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ClearLogsAsync()
    {
        await _log.ClearAsync();
        MessageBox.Show("Журнал очищено.", "Lar’s Cloud", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ClearServiceDataAsync()
    {
        if (MessageBox.Show("Очистити локальну історію й стан синхронізації? Під час наступного запуску файли буде повторно звірено з Google Drive.",
                "Lar’s Cloud", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _database.ClearAsync();
        await RefreshHistoryAsync();
        MessageBox.Show("Локальні службові дані очищено. Файли не видалено.", "Lar’s Cloud",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenLocalFolder()
    {
        if (Directory.Exists(_settings.Current.LocalFolder)) ProcessLauncher.Open(_settings.Current.LocalFolder);
        else MessageBox.Show("Вибрана папка не існує.", "Lar’s Cloud", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OpenDriveFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Current.GoogleDriveWebUrl)) ProcessLauncher.Open(_settings.Current.GoogleDriveWebUrl);
        else MessageBox.Show("Спочатку виконайте першу синхронізацію.", "Lar’s Cloud", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenPrivacy()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "PRIVACY.md");
        if (!string.IsNullOrWhiteSpace(_configuration.PrivacyPolicyUrl) && !_configuration.PrivacyPolicyUrl.Contains("PASTE_"))
            ProcessLauncher.Open(_configuration.PrivacyPolicyUrl);
        else if (File.Exists(local)) ProcessLauncher.Open(local);
    }

    private void RefreshIdentity()
    {
        IsAuthenticated = _oauth.IsAuthenticated;
        AccountDisplay = _oauth.AccountDisplay;
    }

    private void RefreshDates()
    {
        LastSync = Formatters.DateTimeLocal(_settings.Current.LastSyncUtc);
        NextSync = _settings.Current.SyncPaused ? "Призупинено" : _settings.Current.NextSyncUtc is null
            ? "Після першої синхронізації" : Formatters.DateTimeLocal(_settings.Current.NextSyncUtc);
    }

    private void SyncOnProgressChanged(object? sender, SyncProgress progress) => OnUi(() =>
    {
        IsSyncing = progress.Status == SyncRunStatus.Running;
        StatusText = progress.Message;
        StatusBrush = progress.Status switch
        {
            SyncRunStatus.Running => Brush("#F6B84A"),
            SyncRunStatus.Success => Brush("#2ACE7A"),
            SyncRunStatus.Cancelled => Brush("#F6B84A"),
            _ => Brush("#FF5573")
        };
        SyncPercent = progress.Percent;
        SyncProgressText = progress.TotalFiles > 0 ? $"{progress.ProcessedFiles} із {progress.TotalFiles} файлів · {progress.Percent:0}%" : progress.Message;
        CurrentFile = progress.CurrentFile;
        SpeedText = progress.BytesPerSecond > 0 ? $"Швидкість: {Formatters.Bytes((long)progress.BytesPerSecond)}/с" : "";
        EtaText = progress.Remaining is { } eta ? $"Залишилось: {Formatters.Duration(eta)}" : "";
    });

    private void SyncOnCompleted(object? sender, SyncResult result)
    {
        OnUi(() =>
        {
            RefreshDates();
            var shouldNotify = result.Status == SyncRunStatus.Success
                ? _settings.Current.NotificationsEnabled
                : _settings.Current.ErrorNotificationsEnabled;
            if (shouldNotify)
            {
                var title = result.Status == SyncRunStatus.Success ? "Синхронізацію завершено" : "Помилка синхронізації";
                var text = result.Status == SyncRunStatus.Success
                    ? result.UploadedFiles == 0 ? "Нових файлів немає." : $"Завантажено {result.UploadedFiles} файлів ({Formatters.Bytes(result.UploadedBytes)})."
                    : result.Error;
                NotificationRequested?.Invoke(this, new UserNotification(title, text, result.Status == SyncRunStatus.Failed));
            }
        });
        _ = RefreshHistoryAsync();
    }

    private void SyncOnAnalysisCompleted(object? sender, ScanResult scan) => OnUi(() =>
    {
        LocalFileCount = scan.TotalFiles.ToString();
        LocalFolderSize = Formatters.Bytes(scan.TotalBytes);
        ChangedFileCount = scan.ChangedFiles.Count.ToString();
        PendingUploadSize = Formatters.Bytes(scan.UploadBytes);
    });

    private async Task SaveSettingsQuietlyAsync()
    {
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { await _log.ErrorAsync("Could not save settings", ex); }
    }

    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

public sealed record HistoryRow(string Date, string Status, int Files, string Size, string Error, MediaBrush StatusBrush);
public sealed record UserNotification(string Title, string Message, bool IsError);
