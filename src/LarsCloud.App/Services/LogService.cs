using System.Text;
using System.Text.RegularExpressions;
using LarsCloud.Infrastructure;

namespace LarsCloud.Services;

public sealed class LogService
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private const int RetainedFiles = 5;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task InfoAsync(string message) => await WriteAsync("INF", message);
    public async Task WarningAsync(string message) => await WriteAsync("WRN", message);
    public async Task ErrorAsync(string message, Exception? exception = null) =>
        await WriteAsync("ERR", exception is null ? message : $"{message} | {exception.GetType().Name}: {exception.Message}");

    private async Task WriteAsync(string level, string message)
    {
        var clean = Sanitize(message);
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {clean}{Environment.NewLine}";
        await _gate.WaitAsync();
        try
        {
            AppPaths.EnsureCreated();
            RotateIfRequired(Encoding.UTF8.GetByteCount(line));
            await File.AppendAllTextAsync(AppPaths.LogFile, line, new UTF8Encoding(false));
        }
        catch
        {
            // Logging must never terminate the application.
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!Directory.Exists(AppPaths.LogsDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogsDirectory, "larscloud*.log"))
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        finally { _gate.Release(); }
    }

    private static string Sanitize(string message)
    {
        var result = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        foreach (var marker in new[] { "access_token", "refresh_token", "Authorization: Bearer" })
        {
            var index = result.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0) result = result[..index] + marker + "=[REDACTED]";
        }
        result = Regex.Replace(result, @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[EMAIL]", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\b[A-Z]:\\[^|\r\n]*", "[LOCAL_PATH]", RegexOptions.IgnoreCase);
        return result;
    }

    private static void RotateIfRequired(int incomingBytes)
    {
        if (!File.Exists(AppPaths.LogFile)) return;
        if (new FileInfo(AppPaths.LogFile).Length + incomingBytes <= MaxLogBytes) return;

        var oldest = $"{AppPaths.LogFile}.{RetainedFiles}";
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = RetainedFiles - 1; i >= 1; i--)
        {
            var source = $"{AppPaths.LogFile}.{i}";
            var destination = $"{AppPaths.LogFile}.{i + 1}";
            if (File.Exists(source)) File.Move(source, destination, true);
        }
        File.Move(AppPaths.LogFile, $"{AppPaths.LogFile}.1", true);
    }
}
