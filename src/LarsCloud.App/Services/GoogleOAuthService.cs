using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LarsCloud.Models;

namespace LarsCloud.Services;

public sealed class GoogleOAuthService
{
    private readonly ProductConfiguration _configuration;
    private readonly TokenVault _vault;
    private readonly HttpClient _httpClient;
    private readonly LogService _log;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private AuthTokens? _tokens;

    public GoogleOAuthService(ProductConfiguration configuration, TokenVault vault, HttpClient httpClient, LogService log)
    {
        _configuration = configuration;
        _vault = vault;
        _httpClient = httpClient;
        _log = log;
    }

    public bool IsAuthenticated => _tokens is not null && !string.IsNullOrWhiteSpace(_tokens.RefreshToken);
    public string AccountDisplay => _tokens is null ? "Google Drive не підключено"
        : string.IsNullOrWhiteSpace(_tokens.DisplayName) ? _tokens.Email : $"{_tokens.DisplayName} · {_tokens.Email}";
    public event EventHandler? SessionChanged;

    public async Task<bool> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        _tokens = await _vault.LoadAsync(cancellationToken);
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return IsAuthenticated;
    }

    public async Task<AuthTokens> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!_configuration.HasGoogleConfiguration)
            throw new AppConfigurationException("У файлі appsettings.json потрібно вказати GoogleClientId для Desktop OAuth.");

        var port = GetFreeTcpPort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var scopes = _configuration.DriveScopes.Length > 0
            ? _configuration.DriveScopes
            : new[] { "openid", "email", "profile", "https://www.googleapis.com/auth/drive.file" };

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        var authorizationUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + BuildQuery(new Dictionary<string, string>
        {
            ["client_id"] = _configuration.GoogleClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', scopes),
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
            ["include_granted_scopes"] = "true",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state
        });

        ProcessLauncher.Open(authorizationUrl);
        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new OAuthException("Час очікування входу минув. Спробуйте ще раз.");
        }

        var returnedState = context.Request.QueryString["state"];
        var error = context.Request.QueryString["error"];
        var code = context.Request.QueryString["code"];
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(returnedState ?? ""), Encoding.UTF8.GetBytes(state)))
        {
            await RespondToBrowserAsync(context.Response, false, "Перевірка безпеки не пройшла. Поверніться до Lar’s Cloud.");
            throw new OAuthException("OAuth state не збігається.");
        }
        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            await RespondToBrowserAsync(context.Response, false, "Вхід скасовано. Можна закрити цю вкладку.");
            throw new OAuthException(error == "access_denied" ? "Користувач скасував авторизацію." : $"Google OAuth: {error}");
        }

        await RespondToBrowserAsync(context.Response, true, "Google Drive підключено. Можна закрити цю вкладку й повернутися до Lar’s Cloud.");
        var signedInTokens = await ExchangeCodeAsync(code, verifier, redirectUri, cancellationToken);
        await PopulateProfileAsync(signedInTokens, cancellationToken);
        await _vault.SaveAsync(signedInTokens, cancellationToken);
        _tokens = signedInTokens;
        await _log.InfoAsync("Google account connected.");
        SessionChanged?.Invoke(this, EventArgs.Empty);
        return _tokens;
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            _tokens ??= await _vault.LoadAsync(cancellationToken);
            if (_tokens is null || string.IsNullOrWhiteSpace(_tokens.RefreshToken))
                throw new ReauthenticationRequiredException("Google Drive не підключений.");
            if (!forceRefresh && _tokens.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2)) return _tokens.AccessToken;

            var refreshValues = new Dictionary<string, string>
            {
                ["client_id"] = _configuration.GoogleClientId,
                ["refresh_token"] = _tokens.RefreshToken,
                ["grant_type"] = "refresh_token"
            };
            if (!string.IsNullOrWhiteSpace(_configuration.GoogleClientSecret))
                refreshValues["client_secret"] = _configuration.GoogleClientSecret;
            using var content = new FormUrlEncodedContent(refreshValues);

            using var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", content, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (json.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
                {
                    _tokens = null;
                    _vault.Clear();
                    SessionChanged?.Invoke(this, EventArgs.Empty);
                    throw new ReauthenticationRequiredException("Сеанс Google завершився. Підключіть обліковий запис повторно.");
                }
                throw new OAuthException($"Не вдалося оновити Google-токен: HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(json);
            _tokens.AccessToken = document.RootElement.GetProperty("access_token").GetString() ?? "";
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;
            _tokens.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            await _vault.SaveAsync(_tokens, cancellationToken);
            return _tokens.AccessToken;
        }
        finally { _tokenGate.Release(); }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var token = _tokens?.RefreshToken ?? _tokens?.AccessToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
                await _httpClient.PostAsync("https://oauth2.googleapis.com/revoke", content, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        _tokens = null;
        _vault.Clear();
        SessionChanged?.Invoke(this, EventArgs.Empty);
        await _log.InfoAsync("Google account disconnected.");
    }

    private async Task<AuthTokens> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>
        {
            ["client_id"] = _configuration.GoogleClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code"
        };
        if (!string.IsNullOrWhiteSpace(_configuration.GoogleClientSecret)) values["client_secret"] = _configuration.GoogleClientSecret;
        using var response = await _httpClient.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(values), cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new OAuthException($"Google не видав токен: HTTP {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new OAuthException("Google не повернув refresh token. Відкличте доступ Lar’s Cloud у Google Account і увійдіть повторно.");
        return new AuthTokens
        {
            AccessToken = root.GetProperty("access_token").GetString() ?? "",
            RefreshToken = refreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var expiry) ? expiry.GetInt32() : 3600)
        };
    }

    private async Task PopulateProfileAsync(AuthTokens tokens, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        tokens.Email = document.RootElement.TryGetProperty("email", out var email) ? email.GetString() ?? "" : "";
        tokens.DisplayName = document.RootElement.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
    }

    private static async Task RespondToBrowserAsync(HttpListenerResponse response, bool success, string message)
    {
        var color = success ? "#2ACE7A" : "#ff5573";
        var html = $"<!doctype html><meta charset='utf-8'><title>Lar's Cloud</title><body style='margin:0;background:#0B0E22;color:#F5F7F7;font:18px Segoe UI,sans-serif;display:grid;place-items:center;height:100vh'><main style='max-width:620px;padding:48px;border:1px solid #2E5DFC55;border-radius:28px;background:#181C44;text-align:center;box-shadow:0 0 45px #4838CC55'><h1 style='color:{color}'>Lar’s Cloud</h1><p>{WebUtility.HtmlEncode(message)}</p></main></body>";
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string BuildQuery(IEnumerable<KeyValuePair<string, string>> values) =>
        string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class OAuthException : Exception { public OAuthException(string message) : base(message) { } }
public sealed class ReauthenticationRequiredException : Exception { public ReauthenticationRequiredException(string message) : base(message) { } }
public sealed class AppConfigurationException : Exception { public AppConfigurationException(string message) : base(message) { } }
