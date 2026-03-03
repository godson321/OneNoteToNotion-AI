using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OneNoteToNotion.Notion;

namespace OneNoteToNotion.Infrastructure;

public sealed class NotionApiClient : INotionApiClient
{
    private const string NotionVersion = "2022-06-28";
    private const string FileUploadNotionVersion = "2025-09-03";
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(350); // ~3 req/s

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _concurrencyLimiter = new(3, 3);
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private DateTime _lastRequestTime = DateTime.MinValue;

    /// <summary>
    /// Maximum number of retries for rate-limit (429) and transient network errors.
    /// Can be changed at runtime from the UI.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    public NotionApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.notion.com/v1/");
    }

    public async Task<string> CreateChildPageAsync(string parentPageId, string title, string token, CancellationToken cancellationToken)
    {
        var payload = new
        {
            parent = new { page_id = parentPageId },
            properties = new
            {
                title = new
                {
                    title = new[]
                    {
                        new
                        {
                            text = new
                            {
                                content = title
                            }
                        }
                    }
                }
            }
        };

        var response = await SendAsync(HttpMethod.Post, "pages", payload, token, cancellationToken);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("Notion page id is missing.");
    }

    public Task ArchivePagesAsync(IEnumerable<string> pageIds, string token, CancellationToken cancellationToken)
    {
        var tasks = pageIds.Select(pageId =>
            SendAsync(HttpMethod.Patch, $"pages/{pageId}", new { archived = true }, token, cancellationToken));

        return Task.WhenAll(tasks);
    }

    public Task MovePageAsync(string pageId, string newParentPageId, string token, CancellationToken cancellationToken)
    {
        var payload = new
        {
            parent = new
            {
                type = "page_id",
                page_id = newParentPageId
            }
        };

        return SendAsync(HttpMethod.Patch, $"pages/{pageId}", payload, token, cancellationToken);
    }

    public Task AppendBlocksAsync(string pageId, IReadOnlyList<NotionBlockInput> blocks, string token, CancellationToken cancellationToken)
    {
        return AppendBlocksChunkedAsync(pageId, blocks, token, cancellationToken);
    }

    public async Task<List<(string Id, string Title)>> GetChildPagesAsync(string parentPageId, string token, CancellationToken cancellationToken)
    {
        var results = new List<(string Id, string Title)>();
        string? cursor = null;

        do
        {
            var url = $"blocks/{parentPageId}/children?page_size=100";
            if (cursor is not null)
            {
                url += $"&start_cursor={cursor}";
            }

            var response = await SendGetAsync(url, token, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            foreach (var block in root.GetProperty("results").EnumerateArray())
            {
                var type = block.GetProperty("type").GetString();
                if (type == "child_page" && block.TryGetProperty("child_page", out var cp))
                {
                    var id = block.GetProperty("id").GetString() ?? string.Empty;
                    var title = cp.GetProperty("title").GetString() ?? string.Empty;
                    results.Add((id, title));
                }
            }

            cursor = root.GetProperty("has_more").GetBoolean()
                ? root.GetProperty("next_cursor").GetString()
                : null;
        } while (cursor is not null);

        return results;
    }

    #region File Upload (Attachment/Image Sync)

    private const int SinglePartUploadLimitBytes = 20 * 1024 * 1024;
    private const int MultiPartChunkSizeBytes = 20 * 1024 * 1024;

    /// <summary>
    /// 上传文件到 Notion（新 API：create file_upload -> send -> complete[多分片]）。
    /// </summary>
    /// <param name="fileName">文件名</param>
    /// <param name="fileData">文件数据</param>
    /// <param name="token">Notion API Token</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>Notion file_upload 对象 ID（失败返回 null）</returns>
    public async Task<string?> UploadFileAsync(string fileName, byte[] fileData, string token, CancellationToken cancellationToken)
    {
        try
        {
            var uploadedId = await UploadPreparedFileAsync(fileName, fileData, token, cancellationToken);
            if (!string.IsNullOrWhiteSpace(uploadedId))
            {
                return uploadedId;
            }

            return null;
        }
        catch (UnsupportedFileExtensionException)
        {
            // Notion File Upload API 仅支持白名单扩展名。
            // 对不支持的后缀（如 .xml）保留原始字节内容，改名为可上传后缀后重试。
            var fallbackFileName = BuildFallbackFileName(fileName);
            DiagnosticLogger.Warn($"文件扩展名不受支持，改用可上传后缀重试: {fileName} -> {fallbackFileName}");

            try
            {
                var fallbackUploadedId = await UploadPreparedFileAsync(fallbackFileName, fileData, token, cancellationToken);
                if (!string.IsNullOrWhiteSpace(fallbackUploadedId))
                {
                    return fallbackUploadedId;
                }

                DiagnosticLogger.Error($"后缀降级重试后仍上传失败: {fallbackFileName}");
                return null;
            }
            catch (Exception retryEx)
            {
                DiagnosticLogger.Error($"后缀降级重试上传异常: {fallbackFileName}", retryEx);
                return null;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error($"上传文件异常: {fileName}", ex);
            return null;
        }
    }

    private async Task<string?> UploadPreparedFileAsync(string fileName, byte[] fileData, string token, CancellationToken cancellationToken)
    {
        var useMultiPart = fileData.LongLength > SinglePartUploadLimitBytes;
        var uploadInfo = await CreateFileUploadAsync(fileName, fileData.LongLength, useMultiPart, token, cancellationToken);
        if (uploadInfo is null)
        {
            DiagnosticLogger.Error($"创建 file_upload 失败: {fileName}");
            return null;
        }

        var uploaded = useMultiPart
            ? await SendMultiPartUploadAsync(uploadInfo.FileId, fileName, fileData, token, cancellationToken)
            : await SendSinglePartUploadAsync(uploadInfo.FileId, fileName, fileData, token, cancellationToken);

        if (!uploaded)
        {
            DiagnosticLogger.Error($"发送文件内容失败: {fileName}, FileUploadId={uploadInfo.FileId}");
            return null;
        }

        if (useMultiPart)
        {
            var completed = await CompleteMultiPartUploadAsync(uploadInfo.FileId, token, cancellationToken);
            if (!completed)
            {
                DiagnosticLogger.Error($"完成分片上传失败: {fileName}, FileUploadId={uploadInfo.FileId}");
                return null;
            }
        }

        DiagnosticLogger.Info($"文件上传成功: {fileName} -> NotionFileId={uploadInfo.FileId}");
        return uploadInfo.FileId;
    }

    private async Task<FileUploadResponse?> CreateFileUploadAsync(
        string fileName,
        long fileSize,
        bool useMultiPart,
        string token,
        CancellationToken cancellationToken)
    {
        var maxRetries = MaxRetries;

        await _concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                await ThrottleAsync(cancellationToken);

                HttpResponseMessage response;
                try
                {
                    var payload = new Dictionary<string, object?>
                    {
                        ["mode"] = useMultiPart ? "multi_part" : "single_part",
                        ["filename"] = fileName,
                        ["content_type"] = ResolveContentType(fileName)
                    };
                    if (useMultiPart)
                    {
                        payload["number_of_parts"] = GetPartCount(fileSize, MultiPartChunkSizeBytes);
                    }

                    var jsonPayload = JsonSerializer.Serialize(payload);
                    response = await SendRequestAsync(
                        HttpMethod.Post,
                        "file_uploads",
                        jsonPayload,
                        token,
                        cancellationToken,
                        FileUploadNotionVersion);
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"创建 file_upload 网络错误, {delay.TotalSeconds:F1}s 后重试 (attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if ((int)response.StatusCode == 429 && attempt < maxRetries)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"创建 file_upload 429, retry after {retryAfter.TotalSeconds:F1}s");
                    await Task.Delay(retryAfter, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    if ((int)response.StatusCode == 400
                        && responseBody.Contains("extension that is not supported", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnsupportedFileExtensionException(responseBody);
                    }

                    DiagnosticLogger.Error($"创建 file_upload 失败 ({(int)response.StatusCode}): {responseBody}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var fileId = TryGetString(root, "id");
                if (string.IsNullOrWhiteSpace(fileId))
                {
                    DiagnosticLogger.Error($"创建 file_upload 成功但缺少 id: {fileName}");
                    return null;
                }

                return new FileUploadResponse
                {
                    FileId = fileId,
                    FileName = TryGetString(root, "filename") ?? fileName,
                    Status = TryGetString(root, "status")
                };
            }

            DiagnosticLogger.Error($"创建 file_upload 重试 {maxRetries} 次后仍然失败: {fileName}");
            return null;
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private Task<bool> SendSinglePartUploadAsync(
        string fileUploadId,
        string fileName,
        byte[] fileData,
        string token,
        CancellationToken cancellationToken)
    {
        return SendFileUploadPartAsync(fileUploadId, fileName, fileData, null, token, cancellationToken);
    }

    private async Task<bool> SendMultiPartUploadAsync(
        string fileUploadId,
        string fileName,
        byte[] fileData,
        string token,
        CancellationToken cancellationToken)
    {
        var partCount = GetPartCount(fileData.LongLength, MultiPartChunkSizeBytes);

        for (var i = 0; i < partCount; i++)
        {
            var offset = i * MultiPartChunkSizeBytes;
            var length = Math.Min(MultiPartChunkSizeBytes, fileData.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(fileData, offset, chunk, 0, length);

            var sent = await SendFileUploadPartAsync(fileUploadId, fileName, chunk, i + 1, token, cancellationToken);
            if (!sent)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> SendFileUploadPartAsync(
        string fileUploadId,
        string fileName,
        byte[] fileBytes,
        int? partNumber,
        string token,
        CancellationToken cancellationToken)
    {
        var maxRetries = MaxRetries;

        await _concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                await ThrottleAsync(cancellationToken);

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"file_uploads/{fileUploadId}/send");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Add("Notion-Version", FileUploadNotionVersion);

                    using var form = new MultipartFormDataContent();
                    var fileContent = new ByteArrayContent(fileBytes);
                    fileContent.Headers.ContentType = new MediaTypeHeaderValue(ResolveContentType(fileName));
                    form.Add(fileContent, "file", fileName);
                    if (partNumber.HasValue)
                    {
                        form.Add(new StringContent(partNumber.Value.ToString()), "part_number");
                    }

                    request.Content = form;
                    using var response = await _httpClient.SendAsync(request, cancellationToken);

                    if ((int)response.StatusCode == 429 && attempt < maxRetries)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        DiagnosticLogger.Warn($"发送 file_upload 内容 429, retry after {retryAfter.TotalSeconds:F1}s");
                        await Task.Delay(retryAfter, cancellationToken);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        var partInfo = partNumber.HasValue ? $", part={partNumber.Value}" : string.Empty;
                        DiagnosticLogger.Error($"发送 file_upload 内容失败 ({(int)response.StatusCode}): fileUploadId={fileUploadId}{partInfo}, {responseBody}");
                        return false;
                    }

                    return true;
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"发送 file_upload 内容网络错误, {delay.TotalSeconds:F1}s 后重试 (attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                    await Task.Delay(delay, cancellationToken);
                }
            }

            return false;
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<bool> CompleteMultiPartUploadAsync(string fileUploadId, string token, CancellationToken cancellationToken)
    {
        var maxRetries = MaxRetries;

        await _concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                await ThrottleAsync(cancellationToken);

                HttpResponseMessage response;
                try
                {
                    response = await SendRequestAsync(
                        HttpMethod.Post,
                        $"file_uploads/{fileUploadId}/complete",
                        "{}",
                        token,
                        cancellationToken,
                        FileUploadNotionVersion);
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"完成 file_upload 网络错误, {delay.TotalSeconds:F1}s 后重试 (attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if ((int)response.StatusCode == 429 && attempt < maxRetries)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"完成 file_upload 429, retry after {retryAfter.TotalSeconds:F1}s");
                    await Task.Delay(retryAfter, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    DiagnosticLogger.Error($"完成 file_upload 失败 ({(int)response.StatusCode}): {responseBody}");
                    return false;
                }

                return true;
            }

            return false;
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private static int GetPartCount(long fileSize, int partSize)
    {
        return (int)((fileSize + partSize - 1) / partSize);
    }

    private static string ResolveContentType(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            _ => "application/octet-stream"
        };
    }

    private static string BuildFallbackFileName(string originalFileName)
    {
        var trimmedName = string.IsNullOrWhiteSpace(originalFileName)
            ? $"attachment-{Guid.NewGuid():N}"
            : originalFileName.Trim();

        var fileNameOnly = Path.GetFileName(trimmedName);
        if (string.IsNullOrWhiteSpace(fileNameOnly))
        {
            fileNameOnly = $"attachment-{Guid.NewGuid():N}";
        }

        return $"{fileNameOnly}.txt";
    }

    private sealed class UnsupportedFileExtensionException : Exception
    {
        public UnsupportedFileExtensionException(string message)
            : base(message)
        {
        }
    }

    #endregion

    private async Task AppendBlocksChunkedAsync(string pageId, IReadOnlyList<NotionBlockInput> blocks, string token, CancellationToken cancellationToken)
    {
        const int chunkSize = 100;
        for (var i = 0; i < blocks.Count; i += chunkSize)
        {
            var chunkInputs = blocks.Skip(i).Take(chunkSize).ToList();
            var chunk = chunkInputs.Select(ToBlockObject).ToList();
            var payload = new { children = chunk };
            var notionVersion = chunkInputs.Any(IsFileUploadBlock)
                ? FileUploadNotionVersion
                : null;
            await SendAsync(HttpMethod.Patch, $"blocks/{pageId}/children", payload, token, cancellationToken, notionVersion);
        }
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        await _rateLock.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - _lastRequestTime;
            if (elapsed < MinRequestInterval)
            {
                await Task.Delay(MinRequestInterval - elapsed, cancellationToken);
            }
            _lastRequestTime = DateTime.UtcNow;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        object payload,
        string token,
        CancellationToken cancellationToken,
        string? notionVersion = null)
    {
        var jsonPayload = JsonSerializer.Serialize(payload);
        var maxRetries = MaxRetries;

        await _concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                await ThrottleAsync(cancellationToken);

                HttpResponseMessage response;
                try
                {
                    response = await SendRequestAsync(method, url, jsonPayload, token, cancellationToken, notionVersion);
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    // Transient network error (SSL, connection reset, etc.) — retry with longer backoff
                    var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)); // 2s, 4s, 8s, 16s...
                    DiagnosticLogger.Warn($"Notion API 网络错误, {delay.TotalSeconds:F1}s 后重试 (attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if ((int)response.StatusCode != 429)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new InvalidOperationException($"Notion API failed ({(int)response.StatusCode}): {responseBody}");
                    }
                    return response;
                }

                // 429 rate limited - back off
                var retryAfter = response.Headers.RetryAfter?.Delta
                                 ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                DiagnosticLogger.Warn($"Notion API 429 rate limited, retry after {retryAfter.TotalSeconds:F1}s (attempt {attempt + 1}/{maxRetries})");
                await Task.Delay(retryAfter, cancellationToken);
            }

            throw new InvalidOperationException($"Notion API 重试 {maxRetries} 次后仍然失败: {url}");
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<HttpResponseMessage> SendGetAsync(string url, string token, CancellationToken cancellationToken)
    {
        var maxRetries = MaxRetries;

        await _concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                await ThrottleAsync(cancellationToken);

                HttpResponseMessage response;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Add("Notion-Version", NotionVersion);
                    response = await _httpClient.SendAsync(request, cancellationToken);
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"Notion API 网络错误 (GET), {delay.TotalSeconds:F1}s 后重试 (attempt {attempt + 1}/{maxRetries}): {ex.Message}");
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if ((int)response.StatusCode == 429 && attempt < maxRetries)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta
                                     ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"Notion API 429 (GET), retry after {retryAfter.TotalSeconds:F1}s");
                    await Task.Delay(retryAfter, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException($"Notion API GET failed ({(int)response.StatusCode}): {responseBody}");
                }

                return response;
            }

            throw new InvalidOperationException($"Notion API GET 重试 {maxRetries} 次后仍然失败: {url}");
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(
        HttpMethod method,
        string url,
        string jsonPayload,
        string token,
        CancellationToken cancellationToken,
        string? notionVersion = null)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Notion-Version", notionVersion ?? NotionVersion);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static object ToBlockObject(NotionBlockInput block)
    {
        var payload = new Dictionary<string, object>
        {
            ["object"] = "block",
            ["type"] = block.Type,
            [block.Type] = block.Value
        };

        if (block.Children.Count > 0)
        {
            if (SupportsChildren(block.Type))
            {
                var childBlocks = block.Children.Select(ToBlockObject).ToList();
                // Notion requires nested blocks in top-level `children`,
                // not inside the type payload (e.g. `paragraph.children` is invalid).
                payload["children"] = childBlocks;
            }
            else
            {
                DiagnosticLogger.Warn($"Block 类型不支持 children，已忽略嵌套内容: type={block.Type}, count={block.Children.Count}");
            }
        }

        return payload;
    }

    private static bool SupportsChildren(string type)
    {
        return type is "paragraph"
               or "bulleted_list_item"
               or "numbered_list_item"
               or "to_do"
               or "toggle"
               or "quote"
               or "callout"
               or "table"
               or "synced_block"
               or "template";
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool IsFileUploadBlock(NotionBlockInput block)
    {
        if (!string.Equals(block.Type, "file", StringComparison.Ordinal)
            && !string.Equals(block.Type, "image", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(block.Value));
            var root = doc.RootElement;
            return root.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), "file_upload", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
