using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LarsCloud.Infrastructure;
using LarsCloud.Models;

namespace LarsCloud.Services;

public sealed class UpdateService
{
    private readonly ProductConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly LogService _log;

    public UpdateService(ProductConfiguration configuration, HttpClient httpClient, LogService log)
    {
        _configuration = configuration;
        _httpClient = httpClient;
        _log = log;
    }

    public Version CurrentVersion
    {
        get
        {
            var value = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
            return new Version(value.Major, value.Minor, Math.Max(0, value.Build));
        }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.HasGitHubConfiguration) return null;
        var url = $"https://api.github.com/repos/{Uri.EscapeDataString(_configuration.GitHubOwner)}/{Uri.EscapeDataString(_configuration.GitHubRepository)}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"LarsCloud/{CurrentVersion.ToString(3)}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || latest.CompareTo(CurrentVersion) <= 0) return null;

        JsonElement? installerAsset = null;
        string? checksumUrl = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            if (string.Equals(name, _configuration.InstallerAssetName, StringComparison.OrdinalIgnoreCase)) installerAsset = asset;
            if (string.Equals(name, _configuration.InstallerAssetName + ".sha256", StringComparison.OrdinalIgnoreCase))
                checksumUrl = asset.GetProperty("browser_download_url").GetString();
        }
        if (installerAsset is null) return null;
        var digest = installerAsset.Value.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString()?.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase) : null;
        if (string.IsNullOrWhiteSpace(digest) && !string.IsNullOrWhiteSpace(checksumUrl))
        {
            var checksumText = await _httpClient.GetStringAsync(checksumUrl, cancellationToken);
            digest = checksumText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        return new UpdateInfo(latest, tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            installerAsset.Value.GetProperty("browser_download_url").GetString() ?? "",
            digest,
            root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "");
    }

    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.Sha256) || update.Sha256.Length != 64)
            throw new InvalidDataException("Реліз не містить SHA-256. Автоматичне встановлення заблоковано з міркувань безпеки.");
        AppPaths.EnsureCreated();
        var target = Path.Combine(AppPaths.UpdatesDirectory, _configuration.InstallerAssetName);
        var temp = target + ".download";
        using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            var buffer = new byte[1024 * 1024];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                copied += read;
                if (length is > 0) progress?.Report(copied * 100d / length.Value);
            }
        }
        await using var verifyStream = File.OpenRead(temp);
        using var sha = SHA256.Create();
        var actual = Convert.ToHexString(await sha.ComputeHashAsync(verifyStream, cancellationToken));
        if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException("Контрольна сума оновлення не збігається. Файл видалено.");
        }
        File.Move(temp, target, true);
        await _log.InfoAsync($"Update {update.Tag} downloaded and verified.");
        return target;
    }

    public static void LaunchInstallerAfterExit(string installerPath, int processId)
    {
        var fullPath = Path.GetFullPath(installerPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Завантажений інсталятор не знайдено.", fullPath);
        if (processId <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));

        var escapedPath = fullPath.Replace("'", "''", StringComparison.Ordinal);
        var command = $"Wait-Process -Id {processId} -ErrorAction SilentlyContinue; " +
                      $"Start-Process -FilePath '{escapedPath}' " +
                      "-ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/UPDATE') -Verb RunAs";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("Не вдалося підготувати запуск інсталятора.");
    }
}
