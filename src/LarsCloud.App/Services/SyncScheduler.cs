using LarsCloud.Models;

namespace LarsCloud.Services;

public sealed class SyncScheduler : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly GoogleOAuthService _oauth;
    private readonly SyncEngine _engine;
    private readonly LogService _log;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;

    public SyncScheduler(SettingsService settings, GoogleOAuthService oauth, SyncEngine engine, LogService log)
    {
        _settings = settings;
        _oauth = oauth;
        _engine = engine;
        _log = log;
        _settings.Changed += (_, _) => Wake();
        _oauth.SessionChanged += (_, _) => Wake();
    }

    public void Start() => _loop ??= Task.Run(RunLoopAsync);
    public void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RunLoopAsync()
    {
        var retryNotBefore = DateTimeOffset.MinValue;
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                var settings = _settings.Current;
                var due = settings.NextSyncUtc ?? DateTimeOffset.UtcNow;
                if (!settings.SyncPaused && _oauth.IsAuthenticated && settings.SyncFolders.Count > 0
                    && settings.SyncFolders.All(folder => Directory.Exists(folder.Path))
                    && due <= DateTimeOffset.UtcNow && retryNotBefore <= DateTimeOffset.UtcNow)
                {
                    var result = await _engine.RunAsync(manual: false, cancellationToken: _shutdown.Token);
                    retryNotBefore = result.Status == SyncRunStatus.Failed
                        ? DateTimeOffset.UtcNow.AddMinutes(15)
                        : DateTimeOffset.MinValue;
                }

                var next = _settings.Current.NextSyncUtc ?? DateTimeOffset.UtcNow.AddMinutes(15);
                if (retryNotBefore > next) next = retryNotBefore;
                var delay = next - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);
                if (delay > TimeSpan.FromMinutes(30)) delay = TimeSpan.FromMinutes(30);
                using var iteration = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var timer = Task.Delay(delay, iteration.Token);
                var wake = _wake.WaitAsync(iteration.Token);
                await Task.WhenAny(timer, wake);
                iteration.Cancel();
                try { await Task.WhenAll(timer, wake); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                await _log.ErrorAsync("Scheduler error", ex);
                try { await Task.Delay(TimeSpan.FromMinutes(5), _shutdown.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _wake.Dispose();
        _shutdown.Dispose();
    }
}
