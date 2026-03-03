using System.Text;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Mapping;
using OneNoteToNotion.Notion;
using OneNoteToNotion.Parsing;

namespace OneNoteToNotion.Application;

public sealed class NotionSyncOrchestrator
{
    private readonly IOneNoteHierarchyProvider _hierarchyProvider;
    private readonly IOneNotePageContentProvider _pageContentProvider;
    private readonly ISemanticDocumentParser _semanticParser;
    private readonly INotionBlockMapper _blockMapper;
    private readonly INotionApiClient _notionApiClient;

    public NotionSyncOrchestrator(
        IOneNoteHierarchyProvider hierarchyProvider,
        IOneNotePageContentProvider pageContentProvider,
        ISemanticDocumentParser semanticParser,
        INotionBlockMapper blockMapper,
        INotionApiClient notionApiClient)
    {
        _hierarchyProvider = hierarchyProvider;
        _pageContentProvider = pageContentProvider;
        _semanticParser = semanticParser;
        _blockMapper = blockMapper;
        _notionApiClient = notionApiClient;
    }

    public Task<IReadOnlyList<OneNoteTreeNode>> LoadHierarchyAsync(CancellationToken cancellationToken)
        => _hierarchyProvider.GetNotebookHierarchyAsync(cancellationToken);

    public async Task<SyncResult> SyncAsync(
        OneNoteTreeNode rootNode,
        SyncOptions options,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult();
        var totalNodes = CountNodes(rootNode);
        var counter = new SyncCounter { Total = totalNodes };
        await SyncNodeAsync(
            rootNode,
            options.ParentPageId,
            options,
            result,
            progress,
            counter,
            CombineOriginalPath(null, rootNode.Name),
            cancellationToken);
        return result;
    }

    public static int CountNodes(OneNoteTreeNode node)
    {
        var count = 1;
        foreach (var child in node.Children)
        {
            count += CountNodes(child);
        }
        return count;
    }

    public static int CountAllNodes(IEnumerable<OneNoteTreeNode> nodes)
    {
        return nodes.Sum(CountNodes);
    }

    public Task ArchivePagesAsync(IEnumerable<string> notionPageIds, string token, CancellationToken cancellationToken)
        => _notionApiClient.ArchivePagesAsync(notionPageIds, token, cancellationToken);

    public async Task<SyncResult> RetrySyncAsync(
        IReadOnlyList<FailedSyncItem> failedItems,
        SyncOptions options,
        IProgress<SyncProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult();
        var total = failedItems.Sum(f => CountNodes(f.Node));
        var counter = new SyncCounter { Total = total };

        foreach (var item in failedItems)
        {
            await SyncNodeAsync(
                item.Node,
                item.ParentPageId,
                options,
                result,
                progress,
                counter,
                CombineOriginalPath(null, item.Node.Name),
                cancellationToken);
        }

        return result;
    }

    public Task MovePagesAsync(IEnumerable<string> notionPageIds, string newParentPageId, string token, CancellationToken cancellationToken)
    {
        var tasks = notionPageIds.Select(id => _notionApiClient.MovePageAsync(id, newParentPageId, token, cancellationToken));
        return Task.WhenAll(tasks);
    }

    private sealed class SyncCounter
    {
        public int Total { get; init; }
        private int _current;
        public int Current => _current;
        public int Increment() => Interlocked.Increment(ref _current);
    }

    /// <summary>
    /// Phase 1: Sequentially walk the tree, read OneNote content (COM, must be on STA thread),
    /// create Notion pages, and fire off AppendBlocks tasks without awaiting them.
    /// Phase 2: Await all pending AppendBlocks tasks.
    /// This keeps COM calls on the main thread while Notion API writes run concurrently.
    /// </summary>
    private async Task SyncNodeAsync(
        OneNoteTreeNode sourceNode,
        string notionParentPageId,
        SyncOptions options,
        SyncResult result,
        IProgress<SyncProgress>? progress,
        SyncCounter counter,
        string originalPath,
        CancellationToken cancellationToken,
        List<Task>? pendingApiTasks = null,
        int depth = 0)
    {
        using var _ = DiagnosticLogger.BeginOriginalPathScope(originalPath);
        cancellationToken.ThrowIfCancellationRequested();

        // The root call owns the pending tasks list and awaits them at the end
        var isRoot = pendingApiTasks is null;
        pendingApiTasks ??= new List<Task>();

        var indent = new string(' ', depth * 2);
        var nodeTypeLabel = sourceNode.NodeType switch
        {
            OneNoteNodeType.Notebook => "笔记本",
            OneNoteNodeType.SectionGroup => "分区组",
            OneNoteNodeType.Section => "分区",
            OneNoteNodeType.Page => "页面",
            _ => sourceNode.NodeType.ToString()
        };

        try
        {
            var notionPageTitle = sourceNode.Name;
            var ts = DateTime.Now.ToString("HH:mm:ss");
            var deletedDuplicates = 0;

            // Check for existing same-title pages and archive them before creating
            if (!options.DryRun)
            {
                var existingChildren = await _notionApiClient.GetChildPagesAsync(
                    notionParentPageId, options.NotionToken, cancellationToken);
                var duplicates = existingChildren
                    .Where(c => string.Equals(c.Title, notionPageTitle, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Id)
                    .ToList();

                if (duplicates.Count > 0)
                {
                    deletedDuplicates = duplicates.Count;
                    DiagnosticLogger.Info($"[{ts}] [{nodeTypeLabel}] 「{notionPageTitle}」 发现 {deletedDuplicates} 个同名旧页面，正在删除...");
                    await _notionApiClient.ArchivePagesAsync(duplicates, options.NotionToken, cancellationToken);
                    DiagnosticLogger.Info($"[{ts}] [{nodeTypeLabel}] 「{notionPageTitle}」 删除完成");
                }
            }

            var createdPageId = options.DryRun
                ? $"dry-{sourceNode.Id}"
                : await _notionApiClient.CreateChildPageAsync(
                    notionParentPageId,
                    notionPageTitle,
                    options.NotionToken,
                    cancellationToken);

            // Report progress after page is actually created on Notion
            var current = counter.Increment();
            progress?.Report(new SyncProgress(current, counter.Total, notionPageTitle));

            Interlocked.Increment(ref result.CreatedPages);
            lock (result.SyncedPageMap)
            {
                result.SyncedPageMap[sourceNode.Id] = createdPageId;
            }

            ts = DateTime.Now.ToString("HH:mm:ss");

            if (sourceNode.NodeType == OneNoteNodeType.Page)
            {
                // COM call — runs on STA main thread (sequential)
                var pageXml = await _pageContentProvider.GetPageContentXmlAsync(sourceNode.Id, cancellationToken);
                var semanticDocument = _semanticParser.Parse(pageXml);

                // 处理媒体上传（图片/附件；如果有且不是预演模式）
                if (!options.DryRun)
                {
                    await ProcessImagesAsync(semanticDocument, options.NotionToken, cancellationToken);
                    await ProcessAttachmentsAsync(semanticDocument, options.NotionToken, cancellationToken);
                }

                var notionBlocks = _blockMapper.Map(semanticDocument);

                DumpDiagnostics(notionPageTitle, pageXml, semanticDocument);

                var prefix = options.DryRun ? "[预演]" : "[同步]";
                var dupInfo = deletedDuplicates > 0 ? $" (已删除{deletedDuplicates}个旧页)" : "";
                var logEntry = $"[{ts}] {prefix} {indent}{nodeTypeLabel}：「{notionPageTitle}」{dupInfo} → {notionBlocks.Count} 个内容块 [{current}/{counter.Total}]";
                result.LogEntries.Add(logEntry);
                DiagnosticLogger.Info(logEntry);

                if (!options.DryRun && notionBlocks.Count > 0)
                {
                    // Fire-and-forget: let Notion API write run in background
                    // while we continue reading the next page from OneNote COM
                    var appendTask = AppendBlocksWithErrorHandling(
                        createdPageId, notionBlocks, notionPageTitle,
                        sourceNode, notionParentPageId,
                        options, result, cancellationToken);
                    pendingApiTasks.Add(appendTask);
                }

                Interlocked.Increment(ref result.SyncedContentPages);
            }
            else
            {
                var prefix = options.DryRun ? "[预演]" : "[同步]";
                var dupInfo = deletedDuplicates > 0 ? $" (已删除{deletedDuplicates}个旧页)" : "";
                var logEntry = $"[{ts}] {prefix} {indent}{nodeTypeLabel}：「{notionPageTitle}」{dupInfo} [{current}/{counter.Total}]";
                result.LogEntries.Add(logEntry);
                DiagnosticLogger.Info(logEntry);
            }

            // Children are processed sequentially to keep COM calls on the STA thread
            foreach (var child in sourceNode.Children)
            {
                await SyncNodeAsync(
                    child,
                    createdPageId,
                    options,
                    result,
                    progress,
                    counter,
                    CombineOriginalPath(originalPath, child.Name),
                    cancellationToken,
                    pendingApiTasks,
                    depth + 1);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            var logEntry = $"[{ts}] [失败] {indent}{nodeTypeLabel}：「{sourceNode.Name}」 → {ex.Message}";
            DiagnosticLogger.Error(logEntry, ex);
            result.LogEntries.Add(logEntry);
            result.FailedPages.Add(new FailedSyncItem(sourceNode, notionParentPageId, ex.Message));
        }

        // Root call: wait for all background Notion API writes to complete
        if (isRoot && pendingApiTasks.Count > 0)
        {
            await Task.WhenAll(pendingApiTasks);
        }
    }

    private async Task AppendBlocksWithErrorHandling(
        string createdPageId,
        IReadOnlyList<NotionBlockInput> notionBlocks,
        string pageTitle,
        OneNoteTreeNode sourceNode,
        string notionParentPageId,
        SyncOptions options,
        SyncResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await _notionApiClient.AppendBlocksAsync(
                createdPageId,
                notionBlocks,
                options.NotionToken,
                cancellationToken);
            DiagnosticLogger.Info($"[{DateTime.Now:HH:mm:ss}] [写入完成] 「{pageTitle}」 {notionBlocks.Count} 个内容块已写入");
        }
        catch (OperationCanceledException)
        {
            // Don't record cancellation as a failure
        }
        catch (Exception ex)
        {
            var logEntry = $"[{DateTime.Now:HH:mm:ss}] [写入失败] 「{pageTitle}」 → {ex.Message}";
            DiagnosticLogger.Error(logEntry, ex);
            result.LogEntries.Add(logEntry);
            result.FailedPages.Add(new FailedSyncItem(sourceNode, notionParentPageId,
                $"内容写入失败: {ex.Message}"));
        }
    }

    /// <summary>
    /// 处理所有图片上传
    /// </summary>
    private async Task ProcessImagesAsync(SemanticDocument document, string token, CancellationToken cancellationToken)
    {
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (document.Blocks[i] is not ImageBlock image) continue;
            if (!string.IsNullOrWhiteSpace(image.SyncError)) continue; // 已有错误，跳过
            if (!string.IsNullOrWhiteSpace(image.NotionFileId)) continue; // 已上传，跳过

            try
            {
                if (!TryExtractImageBytes(image.DataUri, out var imageBytes, out var format))
                {
                    var errorMessage = "图片数据无效";
                    SetImageSyncError(document, i, image, errorMessage);
                    DiagnosticLogger.Warn($"图片无效，跳过上传: format={image.OriginalFormat}, size={image.FinalSize}");
                    continue;
                }

                var extension = NormalizeImageExtension(format, image.OriginalFormat);
                var fileName = $"onenote-image-{Guid.NewGuid():N}.{extension}";
                DiagnosticLogger.Info($"上传图片: {fileName}, Size={imageBytes.Length} bytes");

                var notionFileId = await _notionApiClient.UploadFileAsync(
                    fileName,
                    imageBytes,
                    token,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(notionFileId))
                {
                    document.Blocks[i] = image with { NotionFileId = notionFileId };
                    DiagnosticLogger.Info($"图片上传成功: {fileName} -> {notionFileId}");
                }
                else
                {
                    var errorMessage = "图片上传失败";
                    SetImageSyncError(document, i, image, errorMessage);
                    DiagnosticLogger.Warn($"图片上传失败: {fileName}");
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"图片处理异常: {ex.Message}";
                SetImageSyncError(document, i, image, errorMessage);
                DiagnosticLogger.Error("处理图片异常", ex);
            }
        }
    }

    /// <summary>
    /// 处理所有附件上传
    /// </summary>
    private async Task ProcessAttachmentsAsync(SemanticDocument document, string token, CancellationToken cancellationToken)
    {
        for (var i = 0; i < document.Blocks.Count; i++)
        {
            if (document.Blocks[i] is not AttachmentBlock attachment) continue;
            if (!string.IsNullOrWhiteSpace(attachment.ErrorMessage)) continue; // 已有错误，跳过
            if (!string.IsNullOrWhiteSpace(attachment.NotionFileName)) continue; // 已上传，跳过

            try
            {
                // 获取文件数据
                byte[]? fileData = null;
                if (!string.IsNullOrWhiteSpace(attachment.FileDataBase64))
                {
                    fileData = Convert.FromBase64String(attachment.FileDataBase64);
                }
                else if (!string.IsNullOrWhiteSpace(attachment.PathCache) && File.Exists(attachment.PathCache))
                {
                    fileData = await File.ReadAllBytesAsync(attachment.PathCache, cancellationToken);
                }

                if (fileData is null)
                {
                    var errorMessage = "附件无可用数据";
                    SetAttachmentError(document, i, attachment, errorMessage);
                    DiagnosticLogger.Warn($"附件无数据，跳过上传: {attachment.FileName}");
                    continue;
                }

                // 上传到 Notion
                DiagnosticLogger.Info($"上传附件: {attachment.FileName}, Size={fileData.Length} bytes");
                var notionFileRefId = await _notionApiClient.UploadFileAsync(
                    attachment.FileName,
                    fileData,
                    token,
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(notionFileRefId))
                {
                    document.Blocks[i] = attachment with { NotionFileName = notionFileRefId };
                    DiagnosticLogger.Info($"附件上传成功: {attachment.FileName} -> {notionFileRefId}");
                }
                else
                {
                    var errorMessage = "附件上传失败";
                    SetAttachmentError(document, i, attachment, errorMessage);
                    DiagnosticLogger.Warn($"附件上传失败: {attachment.FileName}");
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"处理异常: {ex.Message}";
                SetAttachmentError(document, i, attachment, errorMessage);
                DiagnosticLogger.Error($"处理附件异常: {attachment.FileName}", ex);
            }
        }
    }

    private static void SetAttachmentError(SemanticDocument document, int index, AttachmentBlock attachment, string errorMessage)
    {
        if (index >= 0 && index < document.Blocks.Count)
        {
            document.Blocks[index] = attachment with { ErrorMessage = errorMessage };
        }
    }

    private static void SetImageSyncError(SemanticDocument document, int index, ImageBlock image, string errorMessage)
    {
        if (index >= 0 && index < document.Blocks.Count)
        {
            document.Blocks[index] = image with { SyncError = errorMessage };
        }
    }

    private static bool TryExtractImageBytes(string dataUri, out byte[] bytes, out string format)
    {
        bytes = Array.Empty<byte>();
        format = "png";

        if (string.IsNullOrWhiteSpace(dataUri))
        {
            return false;
        }

        var commaIndex = dataUri.IndexOf(',');
        if (commaIndex <= 0 || commaIndex >= dataUri.Length - 1)
        {
            return false;
        }

        var header = dataUri[..commaIndex];
        var payload = dataUri[(commaIndex + 1)..];

        var slashIndex = header.IndexOf('/');
        var semicolonIndex = header.IndexOf(';');
        if (slashIndex > 0 && semicolonIndex > slashIndex)
        {
            format = header[(slashIndex + 1)..semicolonIndex].ToLowerInvariant();
        }

        try
        {
            bytes = Convert.FromBase64String(payload);
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeImageExtension(string formatFromDataUri, string originalFormat)
    {
        var normalized = !string.IsNullOrWhiteSpace(formatFromDataUri)
            ? formatFromDataUri
            : originalFormat;

        normalized = normalized.ToLowerInvariant();
        return normalized switch
        {
            "jpeg" => "jpg",
            "jpg" => "jpg",
            "png" => "png",
            "gif" => "gif",
            "bmp" => "bmp",
            "webp" => "webp",
            _ => "jpg"
        };
    }

    private static string CombineOriginalPath(string? parentPath, string nodeName)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return nodeName;
        }

        return $"{parentPath}/{nodeName}";
    }

    private static void DumpDiagnostics(string pageTitle, string rawXml, SemanticDocument semanticDoc)
    {
        try
        {
            var dumpDir = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "diagnostics");
            Directory.CreateDirectory(dumpDir);

            var safeTitle = string.Join("_", pageTitle.Split(Path.GetInvalidFileNameChars()));
            var timestamp = DateTime.Now.ToString("HHmmss");

            // Dump raw OneNote XML
            var xmlPath = Path.Combine(dumpDir, $"{safeTitle}_{timestamp}_raw.xml");
            File.WriteAllText(xmlPath, rawXml, Encoding.UTF8);

            // Dump parsed semantic blocks
            var sb = new StringBuilder();
            sb.AppendLine($"Title: {semanticDoc.Title}");
            sb.AppendLine($"Block count: {semanticDoc.Blocks.Count}");
            sb.AppendLine(new string('=', 60));

            foreach (var block in semanticDoc.Blocks)
            {
                switch (block)
                {
                    case HeadingBlock h:
                        sb.AppendLine($"[Heading L{h.Level}] {h.Text}");
                        if (h.Runs is not null)
                        {
                            foreach (var r in h.Runs)
                                sb.AppendLine($"  Run: \"{r.Text}\" fg={r.Style.ForegroundColor} bg={r.Style.BackgroundColor} bold={r.Style.Bold} italic={r.Style.Italic}");
                        }
                        break;
                    case ParagraphBlock p:
                        sb.AppendLine($"[Paragraph] align={p.Alignment} layout={p.Layout}");
                        foreach (var r in p.Runs)
                            sb.AppendLine($"  Run: \"{r.Text}\" fg={r.Style.ForegroundColor} bg={r.Style.BackgroundColor} bold={r.Style.Bold} italic={r.Style.Italic} link={r.Link}");
                        break;
                    case TableBlock t:
                        sb.AppendLine($"[Table] rows={t.Rows.Count} cols={t.Rows.Max(r => r.Count)}");
                        for (var ri = 0; ri < t.Rows.Count; ri++)
                        {
                            for (var ci = 0; ci < t.Rows[ri].Count; ci++)
                            {
                                var cellRuns = t.Rows[ri][ci];
                                foreach (var r in cellRuns)
                                    sb.AppendLine($"  [{ri},{ci}] \"{r.Text}\" fg={r.Style.ForegroundColor} bg={r.Style.BackgroundColor} bold={r.Style.Bold}");
                            }
                        }
                        break;
                    case BulletedListBlock bl:
                        sb.AppendLine($"[BulletList] items={bl.Items.Count}");
                        foreach (var item in bl.Items)
                            foreach (var r in item)
                                sb.AppendLine($"  Run: \"{r.Text}\" fg={r.Style.ForegroundColor} bg={r.Style.BackgroundColor}");
                        break;
                    case NumberedListBlock nl:
                        sb.AppendLine($"[NumberedList] items={nl.Items.Count}");
                        foreach (var item in nl.Items)
                            foreach (var r in item)
                                sb.AppendLine($"  Run: \"{r.Text}\" fg={r.Style.ForegroundColor} bg={r.Style.BackgroundColor}");
                        break;
                    case ImageBlock img:
                        sb.AppendLine($"[Image] Caption={img.Caption}, Format={img.OriginalFormat}, OriginalSize={img.OriginalSize}, FinalSize={img.FinalSize}, Processed={img.IsProcessed}, Warning={img.ProcessingError}, DataUriLength={img.DataUri.Length}, NotionFileId={img.NotionFileId}, SyncError={img.SyncError}");
                        break;
                    case AttachmentBlock att:
                        sb.AppendLine($"[Attachment] FileName={att.FileName}, MimeType={att.MimeType}, FileSize={att.FileSize}, HasData={!string.IsNullOrWhiteSpace(att.FileDataBase64)}");
                        break;
                    default:
                        sb.AppendLine($"[{block.GetType().Name}]");
                        break;
                }
            }

            var semanticPath = Path.Combine(dumpDir, $"{safeTitle}_{timestamp}_semantic.txt");
            File.WriteAllText(semanticPath, sb.ToString(), Encoding.UTF8);

            DiagnosticLogger.Info($"诊断文件已保存: {xmlPath} / {semanticPath}");
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"保存诊断文件失败: {ex.Message}");
        }
    }
}
