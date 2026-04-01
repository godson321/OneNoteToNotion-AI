using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneNoteToNotion.Domain;
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
    private int _privateApiConfigWarningEmitted;

    /// <summary>
    /// Maximum number of retries for rate-limit (429) and transient network errors.
    /// Can be changed at runtime from the UI.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
    public string? PrivateApiTokenV2 { get; set; }
    public string? PrivateApiSpaceId { get; set; }
    public string? PrivateApiUserId { get; set; }

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

    public Task AppendBlocksAsync(
        string pageId,
        IReadOnlyList<NotionBlockInput> blocks,
        string token,
        TableCellColorMappingMode tableCellColorMappingMode,
        CancellationToken cancellationToken)
    {
        return AppendBlocksChunkedAsync(pageId, blocks, token, tableCellColorMappingMode, cancellationToken);
    }

    public async Task<string?> TryResolvePrivateSpaceIdFromPageAsync(
        string pageId,
        string tokenV2,
        CancellationToken cancellationToken)
    {
        var normalizedToken = NormalizePrivateToken(tokenV2);
        if (string.IsNullOrWhiteSpace(pageId) || string.IsNullOrWhiteSpace(normalizedToken))
        {
            return null;
        }
        await _concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            foreach (var pageIdCandidate in BuildPageIdCandidates(pageId))
            {
                var spaceId = await TryResolveSpaceIdByGetRecordValuesAsync(
                    pageIdCandidate,
                    normalizedToken,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(spaceId))
                {
                    return spaceId;
                }

                spaceId = await TryResolveSpaceIdByLoadPageChunkAsync(
                    pageIdCandidate,
                    normalizedToken,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(spaceId))
                {
                    return spaceId;
                }
            }

            return null;
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
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

    private async Task AppendBlocksChunkedAsync(
        string pageId,
        IReadOnlyList<NotionBlockInput> blocks,
        string token,
        TableCellColorMappingMode tableCellColorMappingMode,
        CancellationToken cancellationToken)
    {
        const int chunkSize = 100;
        for (var i = 0; i < blocks.Count; i += chunkSize)
        {
            var chunkIndex = i / chunkSize;
            var chunkInputs = blocks.Skip(i).Take(chunkSize).ToList();
            if (chunkInputs.Count == 0)
            {
                DiagnosticLogger.Warn($"AppendBlocks chunk 为空，已跳过: pageId={pageId}, chunkIndex={chunkIndex}");
                continue;
            }

            var blockTypeCounts = string.Join(
                ", ",
                chunkInputs
                    .GroupBy(block => block.Type)
                    .Select(group => $"{group.Key}={group.Count()}"));
            DiagnosticLogger.Info(
                $"AppendBlocks chunk 准备发送: pageId={pageId}, chunkIndex={chunkIndex}, count={chunkInputs.Count}, types={blockTypeCounts}");
            var chunk = chunkInputs.Select(ToBlockObject).ToList();
            var payload = new { children = chunk };
            var notionVersion = chunkInputs.Any(IsFileUploadBlock)
                ? FileUploadNotionVersion
                : null;
            using var appendResponse = await SendAsync(
                HttpMethod.Patch,
                $"blocks/{pageId}/children",
                payload,
                token,
                cancellationToken,
                notionVersion);

            if (tableCellColorMappingMode == TableCellColorMappingMode.Background)
            {
                await TryApplyTableRowBackgroundColorsAsync(chunkInputs, appendResponse, token, cancellationToken);
            }
        }
    }

    private async Task TryApplyTableRowBackgroundColorsAsync(
        IReadOnlyList<NotionBlockInput> chunkInputs,
        HttpResponseMessage appendResponse,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!TryGetPrivateApiConfig(out var tokenV2, out var spaceId, out var userId, out var privateConfigStatus))
            {
                if (ContainsTableBackgroundRows(chunkInputs)
                    && Interlocked.Exchange(ref _privateApiConfigWarningEmitted, 1) == 0)
                {
                    DiagnosticLogger.Warn($"未配置 Notion 私有 API 凭据（{privateConfigStatus}），整格背景色将不可用。");
                }
                return;
            }

            var responseJson = await appendResponse.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return;
            }

            using var appendDoc = JsonDocument.Parse(responseJson);
            if (!appendDoc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var operations = new List<object>();
            var rowEditTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var pairCount = Math.Min(results.GetArrayLength(), chunkInputs.Count);

            for (var i = 0; i < pairCount; i++)
            {
                var inputBlock = chunkInputs[i];
                if (!string.Equals(inputBlock.Type, "table", StringComparison.Ordinal))
                {
                    continue;
                }

                var rowColors = ExtractTableRowBackgroundColors(inputBlock);
                if (rowColors.All(static c => string.IsNullOrWhiteSpace(c)))
                {
                    continue;
                }

                var appendedBlock = results[i];
                var tableId = TryGetString(appendedBlock, "id");
                if (string.IsNullOrWhiteSpace(tableId))
                {
                    continue;
                }

                var rowIds = await GetChildBlockIdsAsync(
                    tableId,
                    "table_row",
                    token,
                    cancellationToken);
                if (rowIds.Count == 0)
                {
                    continue;
                }

                var rowCount = Math.Min(rowColors.Count, rowIds.Count);
                for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
                {
                    var color = rowColors[rowIndex];
                    if (string.IsNullOrWhiteSpace(color))
                    {
                        continue;
                    }

                    operations.AddRange(BuildPrivateBlockColorOperations(
                        rowIds[rowIndex],
                        spaceId,
                        color,
                        rowEditTs,
                        userId));
                }
            }

            if (operations.Count == 0)
            {
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            using var privateCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await SendPrivateSaveTransactionsFanoutAsync(
                operations,
                tokenV2,
                spaceId,
                userId,
                privateCts.Token);
        }
        catch (Exception ex)
        {
            // 私有 API 失败不影响主同步链路，仅失去整格背景增强。
            DiagnosticLogger.Warn($"saveTransactionsFanout 应用表格行背景失败，已回退为无整格背景: {ex.Message}");
        }
    }

    private async Task SendPrivateSaveTransactionsFanoutAsync(
        IReadOnlyList<object> operations,
        string tokenV2,
        string spaceId,
        string? userId,
        CancellationToken cancellationToken)
    {
        if (operations.Count == 0)
        {
            return;
        }

        var payload = new
        {
            requestId = Guid.NewGuid().ToString(),
            transactions = new[]
            {
                new
                {
                    id = Guid.NewGuid().ToString(),
                    spaceId,
                    debug = new { userAction = "actionRegistry.createSimpleTableColorAction" },
                    operations
                }
            },
            unretryable_error_behavior = "continue"
        };

        var jsonPayload = JsonSerializer.Serialize(payload);
        var maxRetries = MaxRetries;

        await _concurrencyLimiter.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                await ThrottleAsync(cancellationToken);

                using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.notion.so/api/v3/saveTransactionsFanout")
                {
                    Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("Cookie", $"token_v2={tokenV2}");
                if (!string.IsNullOrWhiteSpace(userId))
                {
                    request.Headers.TryAddWithoutValidation("x-notion-active-user-header", userId);
                }
                request.Headers.TryAddWithoutValidation("x-notion-space-id", spaceId);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, cancellationToken);
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"saveTransactionsFanout 网络错误, {delay.TotalSeconds:F1}s 后重试: {ex.Message}");
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                using (response)
                {
                    if ((int)response.StatusCode == 429 && attempt < maxRetries)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        await Task.Delay(retryAfter, cancellationToken);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        throw new InvalidOperationException($"saveTransactionsFanout failed ({(int)response.StatusCode}): {body}");
                    }
                }

                return;
            }

            throw new InvalidOperationException($"saveTransactionsFanout 重试 {maxRetries} 次后仍失败");
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private static IEnumerable<object> BuildPrivateBlockColorOperations(
        string blockId,
        string spaceId,
        string color,
        long editedTs,
        string? userId)
    {
        var args = new Dictionary<string, object>
        {
            ["block_color"] = color
        };

        yield return new
        {
            pointer = new
            {
                table = "block",
                id = blockId,
                spaceId
            },
            path = new[] { "format" },
            command = "update",
            args
        };

        if (string.IsNullOrWhiteSpace(userId))
        {
            yield break;
        }

        yield return new
        {
            pointer = new
            {
                table = "block",
                id = blockId,
                spaceId
            },
            path = Array.Empty<string>(),
            command = "update",
            args = new
            {
                last_edited_time = editedTs,
                last_edited_by_id = userId,
                last_edited_by_table = "notion_user"
            }
        };
    }

    private static List<string?> ExtractTableRowBackgroundColors(NotionBlockInput tableBlock)
    {
        if (tableBlock.TableRowBackgroundColors is { Count: > 0 })
        {
            return tableBlock.TableRowBackgroundColors.ToList();
        }

        var rowColors = new List<string?>(tableBlock.Children.Count);
        foreach (var rowBlock in tableBlock.Children)
        {
            rowColors.Add(ExtractUniformRowBackgroundColor(rowBlock.Value));
        }

        return rowColors;
    }

    private static string? ExtractUniformRowBackgroundColor(object rowValue)
    {
        using var rowDoc = JsonDocument.Parse(JsonSerializer.Serialize(rowValue));
        if (!rowDoc.RootElement.TryGetProperty("cells", out var cells)
            || cells.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? uniformColor = null;
        foreach (var cell in cells.EnumerateArray())
        {
            var cellColor = ExtractCellBackgroundColor(cell);
            if (string.IsNullOrWhiteSpace(cellColor))
            {
                return null;
            }

            if (uniformColor is null)
            {
                uniformColor = cellColor;
                continue;
            }

            if (!string.Equals(uniformColor, cellColor, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return uniformColor;
    }

    private static string? ExtractCellBackgroundColor(JsonElement cellElement)
    {
        if (cellElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var richText in cellElement.EnumerateArray())
        {
            if (!richText.TryGetProperty("annotations", out var annotations)
                || !annotations.TryGetProperty("color", out var colorElement))
            {
                continue;
            }

            var color = colorElement.GetString();
            if (!string.IsNullOrWhiteSpace(color)
                && color.EndsWith("_background", StringComparison.Ordinal))
            {
                return color;
            }
        }

        return null;
    }

    private async Task<List<string>> GetChildBlockIdsAsync(
        string parentBlockId,
        string type,
        string token,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        string? cursor = null;

        do
        {
            var url = $"blocks/{parentBlockId}/children?page_size=100";
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                url += $"&start_cursor={cursor}";
            }

            var response = await SendGetAsync(url, token, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in results.EnumerateArray())
                {
                    var blockType = TryGetString(block, "type");
                    if (!string.Equals(blockType, type, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var blockId = TryGetString(block, "id");
                    if (!string.IsNullOrWhiteSpace(blockId))
                    {
                        ids.Add(blockId);
                    }
                }
            }

            cursor = root.GetProperty("has_more").GetBoolean()
                ? root.GetProperty("next_cursor").GetString()
                : null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return ids;
    }

    private static string? TryExtractSpaceIdFromLoadPageChunk(JsonElement root, string normalizedPageId)
    {
        if (!root.TryGetProperty("recordMap", out var recordMap)
            || recordMap.ValueKind != JsonValueKind.Object
            || !recordMap.TryGetProperty("block", out var blocks)
            || blocks.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var blockEntry in blocks.EnumerateObject())
        {
            if (blockEntry.Value.ValueKind != JsonValueKind.Object
                || !blockEntry.Value.TryGetProperty("value", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var blockId = TryGetString(valueElement, "id");
            var normalizedBlockId = NormalizePageId(blockId);
            if (!string.Equals(normalizedBlockId, normalizedPageId, StringComparison.Ordinal)
                && !string.Equals(NormalizePageId(blockEntry.Name), normalizedPageId, StringComparison.Ordinal))
            {
                continue;
            }

            var spaceId = TryGetString(valueElement, "space_id");
            if (!string.IsNullOrWhiteSpace(spaceId))
            {
                return spaceId;
            }
        }

        foreach (var blockEntry in blocks.EnumerateObject())
        {
            if (blockEntry.Value.ValueKind != JsonValueKind.Object
                || !blockEntry.Value.TryGetProperty("value", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var spaceId = TryGetString(valueElement, "space_id");
            if (!string.IsNullOrWhiteSpace(spaceId))
            {
                return spaceId;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveSpaceIdByGetRecordValuesAsync(
        string pageIdCandidate,
        string tokenV2,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            requests = new[]
            {
                new
                {
                    id = pageIdCandidate,
                    table = "block"
                }
            }
        };

        var responseText = await SendPrivateApiRequestAsync(
            "https://www.notion.so/api/v3/getRecordValues",
            payload,
            tokenV2,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(responseText);
        return TryExtractSpaceIdFromGetRecordValues(doc.RootElement, NormalizePageId(pageIdCandidate));
    }

    private async Task<string?> TryResolveSpaceIdByLoadPageChunkAsync(
        string pageIdCandidate,
        string tokenV2,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            pageId = pageIdCandidate,
            limit = 1,
            cursor = new
            {
                stack = Array.Empty<object>()
            }
        };

        var responseText = await SendPrivateApiRequestAsync(
            "https://www.notion.so/api/v3/loadPageChunk",
            payload,
            tokenV2,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(responseText);
        return TryExtractSpaceIdFromLoadPageChunk(doc.RootElement, NormalizePageId(pageIdCandidate));
    }

    private async Task<string?> SendPrivateApiRequestAsync(
        string url,
        object payload,
        string tokenV2,
        CancellationToken cancellationToken)
    {
        var jsonPayload = JsonSerializer.Serialize(payload);
        await ThrottleAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Cookie", $"token_v2={tokenV2}");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return body;
        }

        DiagnosticLogger.Warn(
            $"私有 API 调用失败: {url}, status={(int)response.StatusCode}, body={TruncateForLog(body, 300)}");
        return null;
    }

    private static string? TryExtractSpaceIdFromGetRecordValues(JsonElement root, string normalizedPageId)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("value", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var recordId = TryGetString(valueElement, "id");
            if (!string.Equals(NormalizePageId(recordId), normalizedPageId, StringComparison.Ordinal))
            {
                continue;
            }

            var spaceId = TryGetString(valueElement, "space_id");
            if (!string.IsNullOrWhiteSpace(spaceId))
            {
                return spaceId;
            }
        }

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("value", out var valueElement)
                || valueElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var spaceId = TryGetString(valueElement, "space_id");
            if (!string.IsNullOrWhiteSpace(spaceId))
            {
                return spaceId;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildPageIdCandidates(string rawPageId)
    {
        var normalized = NormalizePageId(rawPageId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        var candidates = new List<string>(2);
        if (normalized.Length == 32 && normalized.All(Uri.IsHexDigit))
        {
            candidates.Add(
                $"{normalized[..8]}-{normalized[8..12]}-{normalized[12..16]}-{normalized[16..20]}-{normalized[20..32]}");
        }
        candidates.Add(normalized);
        return candidates;
    }

    private static string TruncateForLog(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength)
        {
            return trimmed;
        }

        return trimmed[..maxLength] + "...";
    }

    private bool TryGetPrivateApiConfig(out string tokenV2, out string spaceId, out string? userId, out string status)
    {
        tokenV2 = NormalizePrivateToken(PrivateApiTokenV2 ?? Environment.GetEnvironmentVariable("NOTION_TOKEN_V2"));
        spaceId = (PrivateApiSpaceId ?? Environment.GetEnvironmentVariable("NOTION_PRIVATE_SPACE_ID") ?? string.Empty).Trim();
        userId = (PrivateApiUserId ?? Environment.GetEnvironmentVariable("NOTION_PRIVATE_USER_ID"))?.Trim();
        var hasToken = !string.IsNullOrWhiteSpace(tokenV2);
        var hasSpaceId = !string.IsNullOrWhiteSpace(spaceId);
        status = hasToken && hasSpaceId
            ? "token_v2/space_id 已配置"
            : hasToken
                ? "缺少 space_id"
                : hasSpaceId
                    ? "缺少 token_v2"
                    : "缺少 token_v2/space_id";
        return hasToken && hasSpaceId;
    }

    private static string NormalizePageId(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return string.Empty;
        }

        return pageId.Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
    }

    private static string NormalizePrivateToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var normalized = token.Trim();
        if (normalized.StartsWith("token_v2=", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["token_v2=".Length..];
        }

        var semicolonIndex = normalized.IndexOf(';');
        if (semicolonIndex > 0)
        {
            normalized = normalized[..semicolonIndex];
        }

        return normalized.Trim();
    }

    private static bool ContainsTableBackgroundRows(IReadOnlyList<NotionBlockInput> chunkInputs)
    {
        return chunkInputs
            .Where(block => string.Equals(block.Type, "table", StringComparison.Ordinal))
            .SelectMany(ExtractTableRowBackgroundColors)
            .Any(color => !string.IsNullOrWhiteSpace(color));
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

                var statusCode = (int)response.StatusCode;
                if (statusCode == 429)
                {
                    // 429 rate limited - back off
                    var retryAfter = response.Headers.RetryAfter?.Delta
                                     ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    DiagnosticLogger.Warn($"Notion API 429 rate limited, retry after {retryAfter.TotalSeconds:F1}s (attempt {attempt + 1}/{maxRetries})");
                    await Task.Delay(retryAfter, cancellationToken);
                    continue;
                }

                if (statusCode is >= 500 and <= 599 && attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt)); // 2s, 4s, 8s, 16s...
                    var hasChildren = jsonPayload.Contains("\"children\"", StringComparison.Ordinal);
                    DiagnosticLogger.Warn(
                        $"Notion API {statusCode} 服务端错误, payloadLen={jsonPayload.Length}, hasChildren={hasChildren}, {delay.TotalSeconds:F1}s 后重试 (attempt {attempt + 1}/{maxRetries})");
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var hasChildren = jsonPayload.Contains("\"children\"", StringComparison.Ordinal);
                    DiagnosticLogger.Warn(
                        $"Notion API 非成功响应: status={statusCode}, payloadLen={jsonPayload.Length}, hasChildren={hasChildren}");
                    var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException($"Notion API failed ({statusCode}): {responseBody}");
                }

                return response;
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
        var payload = new Dictionary<string, object?>
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
                if (string.Equals(block.Type, "table", StringComparison.Ordinal))
                {
                    // Table block requires children inside `table.children`.
                    payload[block.Type] = AttachChildrenToTypePayload(block.Value, childBlocks);
                }
                else
                {
                    payload["children"] = childBlocks;
                }
            }
            else
            {
                DiagnosticLogger.Warn($"Block 类型不支持 children，已忽略嵌套内容: type={block.Type}, count={block.Children.Count}");
            }
        }

        return payload;
    }

    private static object AttachChildrenToTypePayload(object value, IReadOnlyList<object> children)
    {
        var payloadNode = JsonSerializer.SerializeToNode(value) as JsonObject ?? new JsonObject();
        payloadNode["children"] = JsonSerializer.SerializeToNode(children);
        return payloadNode;
    }

    private static bool SupportsChildren(string type)
    {
        return type is "bulleted_list_item"
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
