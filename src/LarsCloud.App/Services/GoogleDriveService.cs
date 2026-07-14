using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LarsCloud.Models;

namespace LarsCloud.Services;

public sealed class GoogleDriveService
{
    private const string ApiBase = "https://www.googleapis.com/drive/v3";
    private const string UploadBase = "https://www.googleapis.com/upload/drive/v3";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private const int ChunkSize = 8 * 1024 * 1024;
    private readonly GoogleOAuthService _oauth;
    private readonly HttpClient _httpClient;
    private readonly LogService _log;
    private readonly Dictionary<string, string> _folderCache = new(StringComparer.OrdinalIgnoreCase);

    public GoogleDriveService(GoogleOAuthService oauth, HttpClient httpClient, LogService log)
    {
        _oauth = oauth;
        _httpClient = httpClient;
        _log = log;
    }

    public async Task<DriveAbout> GetAboutAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/about?fields=user(displayName,emailAddress),storageQuota(limit,usage)"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        var user = root.GetProperty("user");
        var quota = root.GetProperty("storageQuota");
        long? limit = quota.TryGetProperty("limit", out var limitElement) && long.TryParse(limitElement.GetString(), out var parsedLimit)
            ? parsedLimit : null;
        var usage = quota.TryGetProperty("usage", out var usageElement) && long.TryParse(usageElement.GetString(), out var parsedUsage)
            ? parsedUsage : 0;
        return new DriveAbout(
            user.TryGetProperty("displayName", out var displayName) ? displayName.GetString() ?? "" : "",
            user.TryGetProperty("emailAddress", out var email) ? email.GetString() ?? "" : "",
            new DriveQuota(limit, usage));
    }

    public async Task<(DriveFolder Root, DriveFolder Device)> EnsureBackupFoldersAsync(CancellationToken cancellationToken = default)
    {
        var root = await GetOrCreateFolderAsync("Lar's Cloud", "root", cancellationToken);
        var deviceName = SanitizeFileName(Environment.MachineName);
        var device = await GetOrCreateFolderAsync(deviceName, root.Id, cancellationToken);
        _folderCache.Clear();
        _folderCache[""] = device.Id;
        return (root, device);
    }

    public async Task<string> EnsureRelativeFolderAsync(string relativeDirectory, string deviceFolderId,
        CancellationToken cancellationToken = default)
    {
        relativeDirectory = relativeDirectory.Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(relativeDirectory)) return deviceFolderId;
        if (!_folderCache.ContainsKey("")) _folderCache[""] = deviceFolderId;

        var currentPath = "";
        var parentId = deviceFolderId;
        foreach (var segment in relativeDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}/{segment}";
            if (_folderCache.TryGetValue(currentPath, out var cached))
            {
                parentId = cached;
                continue;
            }
            var folder = await GetOrCreateFolderAsync(segment, parentId, cancellationToken);
            parentId = folder.Id;
            _folderCache[currentPath] = parentId;
        }
        return parentId;
    }

    public async Task<DriveFile?> FindFileAsync(string name, string parentId, CancellationToken cancellationToken = default)
    {
        var q = $"name='{EscapeQuery(name)}' and '{EscapeQuery(parentId)}' in parents and trashed=false and mimeType!='{FolderMime}'";
        var url = $"{ApiBase}/files?q={Uri.EscapeDataString(q)}&spaces=drive&pageSize=10&fields=files(id,name,md5Checksum,sha256Checksum,size,webViewLink)";
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var files = document.RootElement.GetProperty("files");
        return files.GetArrayLength() == 0 ? null : ParseDriveFile(files[0]);
    }

    public async Task<DriveFile> UploadFileAsync(LocalFileCandidate file, string parentId, string? remoteId,
        IProgress<FileUploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(file.RelativePath);
        if (string.IsNullOrWhiteSpace(remoteId))
            remoteId = (await FindFileAsync(name, parentId, cancellationToken))?.Id;
        try
        {
            if (file.Size == 0) return await UploadEmptyFileAsync(file, parentId, remoteId, cancellationToken);
            var sessionUri = await StartResumableSessionAsync(file, parentId, remoteId, cancellationToken);
            return await UploadChunksAsync(sessionUri, file, progress, cancellationToken);
        }
        catch (DriveApiException ex) when (!string.IsNullOrWhiteSpace(remoteId) && ex.StatusCode == HttpStatusCode.NotFound)
        {
            // The user may have removed the old remote object manually. Recreate it instead of failing forever.
            if (file.Size == 0) return await UploadEmptyFileAsync(file, parentId, null, cancellationToken);
            var sessionUri = await StartResumableSessionAsync(file, parentId, null, cancellationToken);
            return await UploadChunksAsync(sessionUri, file, progress, cancellationToken);
        }
    }

    public async Task DeleteFileAsync(string driveId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Delete,
            $"{ApiBase}/files/{Uri.EscapeDataString(driveId)}"), cancellationToken);
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<DriveFolder> GetOrCreateFolderAsync(string name, string parentId, CancellationToken cancellationToken)
    {
        var q = $"name='{EscapeQuery(name)}' and '{EscapeQuery(parentId)}' in parents and trashed=false and mimeType='{FolderMime}'";
        var listUrl = $"{ApiBase}/files?q={Uri.EscapeDataString(q)}&spaces=drive&pageSize=10&fields=files(id,name,webViewLink)";
        using (var listResponse = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, listUrl), cancellationToken))
        {
            await EnsureSuccessAsync(listResponse, cancellationToken);
            using var document = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(cancellationToken));
            var files = document.RootElement.GetProperty("files");
            if (files.GetArrayLength() > 0)
            {
                var item = files[0];
                var id = item.GetProperty("id").GetString()!;
                return new DriveFolder(id, name, $"https://drive.google.com/drive/folders/{id}");
            }
        }

        var body = JsonSerializer.Serialize(new { name, mimeType = FolderMime, parents = new[] { parentId } });
        using var createResponse = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Post,
            $"{ApiBase}/files?fields=id,name,webViewLink")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }, cancellationToken);
        await EnsureSuccessAsync(createResponse, cancellationToken);
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync(cancellationToken));
        var createdId = created.RootElement.GetProperty("id").GetString()!;
        await _log.InfoAsync("Created a managed Google Drive folder.");
        return new DriveFolder(createdId, name, $"https://drive.google.com/drive/folders/{createdId}");
    }

    private async Task<Uri> StartResumableSessionAsync(LocalFileCandidate file, string parentId, string? remoteId,
        CancellationToken cancellationToken)
    {
        var isNew = string.IsNullOrWhiteSpace(remoteId);
        var url = isNew
            ? $"{UploadBase}/files?uploadType=resumable&fields=id,name,md5Checksum,sha256Checksum,size,webViewLink"
            : $"{UploadBase}/files/{Uri.EscapeDataString(remoteId!)}?uploadType=resumable&fields=id,name,md5Checksum,sha256Checksum,size,webViewLink";
        var name = Path.GetFileName(file.RelativePath);
        var metadata = isNew
            ? JsonSerializer.Serialize(new
            {
                name,
                parents = new[] { parentId },
                appProperties = new { larsCloudPath = file.RelativePath, larsCloudDevice = Environment.MachineName }
            })
            : JsonSerializer.Serialize(new { name });

        using var response = await SendAuthorizedAsync(() =>
        {
            var request = new HttpRequestMessage(isNew ? HttpMethod.Post : HttpMethod.Patch, url)
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Type", GuessContentType(name));
            request.Headers.TryAddWithoutValidation("X-Upload-Content-Length", file.Size.ToString());
            return request;
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return response.Headers.Location ?? throw new DriveApiException("Google Drive не повернув адресу сесії завантаження.");
    }

    private async Task<DriveFile> UploadEmptyFileAsync(LocalFileCandidate file, string parentId, string? remoteId,
        CancellationToken cancellationToken)
    {
        var isNew = string.IsNullOrWhiteSpace(remoteId);
        var url = isNew
            ? $"{UploadBase}/files?uploadType=multipart&fields=id,name,md5Checksum,sha256Checksum,size,webViewLink"
            : $"{UploadBase}/files/{Uri.EscapeDataString(remoteId!)}?uploadType=multipart&fields=id,name,md5Checksum,sha256Checksum,size,webViewLink";
        var name = Path.GetFileName(file.RelativePath);
        var metadata = isNew
            ? JsonSerializer.Serialize(new
            {
                name,
                parents = new[] { parentId },
                appProperties = new { larsCloudPath = file.RelativePath, larsCloudDevice = Environment.MachineName }
            })
            : JsonSerializer.Serialize(new { name });

        using var response = await SendAuthorizedAsync(() =>
        {
            var multipart = new MultipartContent("related");
            multipart.Add(new StringContent(metadata, Encoding.UTF8, "application/json"));
            var data = new ByteArrayContent(Array.Empty<byte>());
            data.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            multipart.Add(data);
            return new HttpRequestMessage(isNew ? HttpMethod.Post : HttpMethod.Patch, url) { Content = multipart };
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ValidateUploadedFile(file, ParseDriveFile(document.RootElement));
    }

    private async Task<DriveFile> UploadChunksAsync(Uri sessionUri, LocalFileCandidate file,
        IProgress<FileUploadProgress>? progress, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file.FullPath, new FileStreamOptions
        {
            Access = FileAccess.Read,
            Mode = FileMode.Open,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 1024 * 1024
        });
        var buffer = new byte[ChunkSize];
        long position = 0;
        var stalledResponses = 0;

        while (position < file.Size)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = position;
            var requested = (int)Math.Min(buffer.Length, file.Size - position);
            var read = 0;
            while (read < requested)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read, requested - read), cancellationToken);
                if (count == 0) break;
                read += count;
            }
            if (read == 0) throw new IOException("Файл змінився або став недоступним під час завантаження.");

            HttpResponseMessage? response = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    response = await SendUploadChunkAsync(sessionUri, buffer, read, position, file.Size, cancellationToken);
                    if (response.StatusCode == HttpStatusCode.Unauthorized && attempt < 5)
                    {
                        response.Dispose();
                        response = null;
                        await _oauth.GetAccessTokenAsync(forceRefresh: true, cancellationToken: cancellationToken);
                        continue;
                    }
                    if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 5)
                    {
                        response.Dispose();
                        response = null;
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                        continue;
                    }
                    break;
                }
                catch (HttpRequestException) when (attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }
            if (response is null) throw new DriveApiException("Завантаження перервано після кількох повторних спроб.");
            using (response)
            {
                if ((int)response.StatusCode == 308)
                {
                    var committed = ReadCommittedPosition(response, position);
                    if (committed <= position)
                    {
                        stalledResponses++;
                        if (stalledResponses >= 5)
                            throw new DriveApiException("Google Drive не підтверджує отримання частини файлу. Спробу буде повторено пізніше.");
                        await Task.Delay(TimeSpan.FromSeconds(stalledResponses), cancellationToken);
                    }
                    else stalledResponses = 0;
                    position = committed;
                    progress?.Report(new FileUploadProgress(position, file.Size));
                    continue;
                }
                await EnsureSuccessAsync(response, cancellationToken);
                position = file.Size;
                progress?.Report(new FileUploadProgress(position, file.Size));
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return ValidateUploadedFile(file, ParseDriveFile(document.RootElement));
            }
        }
        throw new DriveApiException("Google Drive не підтвердив завершення завантаження.");
    }

    private async Task<HttpResponseMessage> SendUploadChunkAsync(Uri sessionUri, byte[] buffer, int count,
        long start, long total, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, sessionUri);
        var token = await _oauth.GetAccessTokenAsync(cancellationToken: cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new ByteArrayContent(buffer, 0, count);
        request.Content.Headers.ContentLength = count;
        if (total == 0) request.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */0");
        else request.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + count - 1, total);
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        finally { request.Dispose(); }
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                await _oauth.GetAccessTokenAsync(forceRefresh: attempt > 0, cancellationToken: cancellationToken));
            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.Unauthorized || attempt == 1) return response;
            response.Dispose();
        }
        throw new ReauthenticationRequiredException("Потрібно повторно увійти в Google-акаунт.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var friendly = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Потрібно повторно увійти в Google-акаунт.",
            HttpStatusCode.Forbidden when body.Contains("storageQuota", StringComparison.OrdinalIgnoreCase) ||
                                               body.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase)
                => "Недостатньо місця на Google Drive.",
            HttpStatusCode.Forbidden => "Google Drive відхилив доступ до файлу.",
            HttpStatusCode.NotFound => "Файл або папку на Google Drive не знайдено.",
            _ => $"Помилка Google Drive: HTTP {(int)response.StatusCode}."
        };
        throw new DriveApiException(friendly, response.StatusCode);
    }

    private static DriveFile ParseDriveFile(JsonElement item)
    {
        long? size = item.TryGetProperty("size", out var sizeElement) && long.TryParse(sizeElement.GetString(), out var parsed)
            ? parsed : null;
        return new DriveFile(
            item.GetProperty("id").GetString() ?? "",
            item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            item.TryGetProperty("md5Checksum", out var md5) ? md5.GetString() : null,
            item.TryGetProperty("sha256Checksum", out var sha256) ? sha256.GetString() : null,
            size,
            item.TryGetProperty("webViewLink", out var link) ? link.GetString() : null);
    }

    private static DriveFile ValidateUploadedFile(LocalFileCandidate local, DriveFile remote)
    {
        if (remote.Size is long size && size != local.Size)
            throw new DriveApiException("Google Drive повернув інший розмір завантаженого файлу. Спробу буде повторено.");
        if (!string.IsNullOrWhiteSpace(remote.Sha256Checksum)
            && !remote.Sha256Checksum.Equals(local.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new DriveApiException("Контрольна сума завантаженого файла не збігається. Спробу буде повторено.");
        return remote;
    }

    private static long ReadCommittedPosition(HttpResponseMessage response, long fallback)
    {
        if (!response.Headers.TryGetValues("Range", out var ranges)) return fallback;
        var range = ranges.FirstOrDefault();
        var dash = range?.LastIndexOf('-') ?? -1;
        return dash >= 0 && long.TryParse(range![(dash + 1)..], out var last) ? last + 1 : fallback;
    }

    private static string EscapeQuery(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
    private static string SanitizeFileName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static string GuessContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".txt" => "text/plain", ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".zip" => "application/zip", _ => "application/octet-stream"
    };
}

public sealed record FileUploadProgress(long UploadedBytes, long TotalBytes);
public sealed class DriveApiException : Exception
{
    public DriveApiException(string message, HttpStatusCode? statusCode = null) : base(message) => StatusCode = statusCode;
    public HttpStatusCode? StatusCode { get; }
}
