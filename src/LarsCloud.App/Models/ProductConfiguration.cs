using System.Text.Json;

namespace LarsCloud.Models;

public sealed class ProductConfiguration
{
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    public string GitHubOwner { get; set; } = "";
    public string GitHubRepository { get; set; } = "LarsCloud";
    public string InstallerAssetName { get; set; } = "LarsCloud_Setup.exe";
    public string PrivacyPolicyUrl { get; set; } = "";
    public string[] DriveScopes { get; set; } = Array.Empty<string>();

    public bool HasGoogleConfiguration => GoogleClientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase)
                                          && !GoogleClientId.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase);
    public bool HasGitHubConfiguration => !string.IsNullOrWhiteSpace(GitHubOwner)
                                          && !GitHubOwner.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase)
                                          && !string.IsNullOrWhiteSpace(GitHubRepository);

    public static async Task<ProductConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return new ProductConfiguration();
        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<ProductConfiguration>(stream, cancellationToken: cancellationToken)
                            ?? new ProductConfiguration();
        configuration.GoogleClientId = configuration.GoogleClientId?.Trim() ?? "";
        configuration.GoogleClientSecret = configuration.GoogleClientSecret?.Trim() ?? "";
        configuration.GitHubOwner = configuration.GitHubOwner?.Trim() ?? "";
        configuration.GitHubRepository = configuration.GitHubRepository?.Trim() ?? "";
        configuration.DriveScopes = (configuration.DriveScopes ?? Array.Empty<string>())
            .Where(scope => !string.IsNullOrWhiteSpace(scope)).Select(scope => scope.Trim()).ToArray();
        return configuration;
    }
}
