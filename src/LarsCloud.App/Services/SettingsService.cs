using System.Text.Json;
using LarsCloud.Infrastructure;
using LarsCloud.Models;

namespace LarsCloud.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    public AppSettings Current { get; private set; } = new();
    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        AppPaths.EnsureCreated();
        if (!File.Exists(AppPaths.SettingsFile))
        {
            Current = new AppSettings();
            return;
        }

        try
        {
            await using var stream = File.OpenRead(AppPaths.SettingsFile);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken)
                      ?? new AppSettings();
            Current.Normalize();
        }
        catch (JsonException)
        {
            var invalid = AppPaths.SettingsFile + $".invalid-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(AppPaths.SettingsFile, invalid, true);
            Current = new AppSettings();
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Current.Normalize();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            AppPaths.EnsureCreated();
            var temp = AppPaths.SettingsFile + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, Current, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temp, AppPaths.SettingsFile, true);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ResetServiceDataAsync()
    {
        var preservedFolder = Current.LocalFolder;
        Current = new AppSettings { LocalFolder = preservedFolder };
        await SaveAsync();
    }
}
