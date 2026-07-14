using System.Net.Http;

namespace LarsCloud.Services;

public sealed class ConnectivityService
{
    private readonly HttpClient _httpClient;

    public ConnectivityService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/generate_204");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // Any HTTP response proves that DNS/TLS/network access to Google is working.
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
