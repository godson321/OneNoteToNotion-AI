using System.Text;
using System.Net;
using System.Text.RegularExpressions;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Parsing;

namespace OneNoteToNotion.Infrastructure;

/// <summary>
/// 将 OneNote 内容导出为本地 Markdown 文件，兼容 Obsidian 格式。
/// </summary>
public sealed class MarkdownExporter
{
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly Regex TableSegmentRegex = new(
        @"<table\b[^>]*>[\s\S]*?</table>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CorruptedCloserRegex = new(
        @"[?？]\s*/\s*(td|span)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IOneNotePageContentProvider _pageContentProvider;
    private readonly ISemanticDocumentParser _semanticParser;

    public MarkdownExporter(IOneNotePageContentProvider pageContentProvider)
    {
        _pageContentProvider = pageContentProvider;
        _semanticParser = new OneNoteXmlSemanticParser();
    }

    /// <summary>
    /// 导出选中的 OneNote 节点到本地 Markdown 文件。
    /// </summary>
    public async Task<ExportResult> ExportAsync(
        IReadOnlyList<OneNoteTreeNode> nodes,
        string outputPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ExportResult();
        var totalNodes = CountAllNodes(nodes);
        var counter = new ExportCounter { Total = totalNodes };

        try
        {
            PrepareOutputDirectory(outputPath);

            foreach (var node in nodes)
            {
                await ExportNodeAsync(
                    node,
                    outputPath,
                    result,
                    progress,
                    counter,
                    CombineOriginalPath(null, node.Name),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            result.WasCanceled = true;
        }

        return result;
    }

    private async Task ExportNodeAsync(
        OneNoteTreeNode node,
        string parentPath,
        ExportResult result,
        IProgress<ExportProgress>? progress,
        ExportCounter counter,
        string originalPath,
        CancellationToken cancellationToken)
    {
        using var _ = DiagnosticLogger.BeginOriginalPathScope(originalPath);
        cancellationToken.ThrowIfCancellationRequested();

        var current = counter.Increment();
        var safeName = SanitizeFileName(node.Name);
        
        progress?.Report(new ExportProgress
        {
            Current = current,
            Total = counter.Total,
            CurrentNodeName = node.Name,
            Status = $"正在导出: {node.Name}"
        });

        try
        {
            if (node.Children.Count > 0)
            {
                // 有子节点的父节点创建为文件夹。
                var folderPath = Path.Combine(parentPath, safeName);
                Directory.CreateDirectory(folderPath);

                // 如果该节点本身也是一个页面（有内容），则在文件夹内创建 index.md。
                if (node.NodeType == OneNoteNodeType.Page)
                {
                    await ExportPageAsync(node, folderPath, "index.md", result, cancellationToken);
                }

                // 递归导出子节点。
                foreach (var child in node.Children)
                {
                    await ExportNodeAsync(
                        child,
                        folderPath,
                        result,
                        progress,
                        counter,
                        CombineOriginalPath(originalPath, child.Name),
                        cancellationToken);
                }
            }
            else
            {
                // 叶子节点直接导出为 .md 文件。
                if (node.NodeType == OneNoteNodeType.Page)
                {
                    await ExportPageAsync(node, parentPath, $"{safeName}.md", result, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            result.FailedNodes.Add(new FailedExportItem
            {
                Node = node,
                Error = ex.Message
            });
        }
    }

    private async Task ExportPageAsync(OneNoteTreeNode node, string folderPath, string fileName, 
        ExportResult result, CancellationToken cancellationToken)
    {
        try
        {
            var pageContent = await _pageContentProvider.GetPageContentXmlAsync(node.Id, cancellationToken);
            var semanticDoc = _semanticParser.Parse(pageContent);
            
            var renderContext = new MarkdownRenderContext(
                folderPath,
                $"{Path.GetFileNameWithoutExtension(fileName)}_images",
                $"{Path.GetFileNameWithoutExtension(fileName)}_attachments");
            var markdown = RenderSemanticDocumentToMarkdown(semanticDoc, renderContext);
            markdown = RepairAndValidateTableMarkup(markdown, $"{node.Name} ({node.Id})");

            var filePath = Path.Combine(folderPath, fileName);
            await File.WriteAllTextAsync(filePath, markdown, Utf8WithBom, cancellationToken);
            result.ExportedFiles++;
        }
        catch (Exception ex)
        {
            result.FailedNodes.Add(new FailedExportItem
            {
                Node = node,
                Error = $"导出页面失败: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// 将 SemanticDocument 转换为 Markdown。
    /// context 为 null 时，图片和附件保留 data URI；有 context 时优先落地为本地文件。
    /// </summary>
    private string ConvertSemanticDocumentToMarkdown(SemanticDocument doc)
    {
        return RenderSemanticDocumentToMarkdown(doc, context: null);
    }

    private string RenderSemanticDocumentToMarkdown(SemanticDocument doc, MarkdownRenderContext? context)
    {
        var sb = new StringBuilder();
        
        // Obsidian frontmatter
        sb.AppendLine("---");
        sb.AppendLine($"title: {EscapeYaml(doc.Title)}");
        sb.AppendLine($"created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("---");
        sb.AppendLine();
        
        // 页面标题
        sb.AppendLine($"# {doc.Title}");
        sb.AppendLine();

        // 处理所有块
        for (int i = 0; i < doc.Blocks.Count; i++)
        {
            var block = doc.Blocks[i];
            ProcessBlock(block, sb, 0, context);
        }

        return sb.ToString();
    }

    private void ProcessBlock(SemanticBlock block, StringBuilder sb, int indentLevel, MarkdownRenderContext? context)
    {
        switch (block)
        {
            case HeadingBlock heading:
                ProcessHeadingBlock(heading, sb, context);
                break;
            case ParagraphBlock paragraph:
                ProcessParagraphBlock(paragraph, sb, paragraph.IndentLevel, context);
                break;
            case BulletedListBlock bulletList:
                ProcessBulletListBlock(bulletList, sb, context);
                break;
            case NumberedListBlock numberedList:
                ProcessNumberedListBlock(numberedList, sb, context);
                break;
            case TableBlock table:
                ProcessTableBlock(table, sb, context);
                break;
            case ImageBlock image:
                ProcessImageBlock(image, sb, context);
                break;
            case AttachmentBlock attachment:
                ProcessAttachmentBlock(attachment, sb, context);
                break;
            case UnsupportedBlock unsupported:
                ProcessUnsupportedBlock(unsupported, sb);
                break;
            default:
                DiagnosticLogger.Warn($"未知的块类型: {block.GetType().Name}");
                break;
        }
    }

    private void ProcessHeadingBlock(HeadingBlock heading, StringBuilder sb, MarkdownRenderContext? context)
    {
        var level = Math.Clamp(heading.Level, 1, 6);
        var hashes = new string('#', level);
        var text = heading.Runs != null ? RenderTextRuns(heading.Runs, context) : heading.Text;
        sb.AppendLine($"{hashes} {text}");
        sb.AppendLine();
    }

    private void ProcessParagraphBlock(ParagraphBlock paragraph, StringBuilder sb, int indentLevel, MarkdownRenderContext? context)
    {
        var text = RenderTextRuns(paragraph.Runs, context);
        
        if (string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine();
            return;
        }

        var prefix = indentLevel > 0 ? new string(' ', indentLevel * 2) : "";
        
        switch (paragraph.Layout)
        {
            case LayoutHint.AbsolutePositioned:
            case LayoutHint.FloatingObject:
                sb.AppendLine($"> {text}");
                break;
            case LayoutHint.ColumnLike:
                sb.AppendLine("> [!NOTE] 原文包含分栏布局，已降级为顺序段落。");
                AppendAlignedParagraph(paragraph.Alignment, $"{prefix}{text}", sb);
                break;
            default:
                AppendAlignedParagraph(paragraph.Alignment, $"{prefix}{text}", sb);
                break;
        }
        
        sb.AppendLine();
    }

    private void ProcessBulletListBlock(BulletedListBlock bulletList, StringBuilder sb, MarkdownRenderContext? context)
    {
        var prefix = bulletList.IndentLevel > 0 ? new string(' ', bulletList.IndentLevel * 2) : "";
        foreach (var item in bulletList.Items)
        {
            var text = RenderTextRuns(item, context);
            sb.AppendLine($"{prefix}- {text}");
        }
        sb.AppendLine();
    }

    private void ProcessNumberedListBlock(NumberedListBlock numberedList, StringBuilder sb, MarkdownRenderContext? context)
    {
        var prefix = numberedList.IndentLevel > 0 ? new string(' ', numberedList.IndentLevel * 2) : "";
        var number = 1;
        foreach (var item in numberedList.Items)
        {
            var text = RenderTextRuns(item, context);
            sb.AppendLine($"{prefix}{number}. {text}");
            number++;
        }
        sb.AppendLine();
    }

    private static void AppendAlignedParagraph(ParagraphAlignment alignment, string text, StringBuilder sb)
    {
        var normalized = alignment switch
        {
            ParagraphAlignment.Center => "center",
            ParagraphAlignment.Right => "right",
            ParagraphAlignment.Justify => "justify",
            _ => null
        };

        if (normalized is null)
        {
            sb.AppendLine(text);
            return;
        }

        sb.AppendLine($"<div style=\"text-align:{normalized};\">{text}</div>");
    }

    private void ProcessTableBlock(TableBlock table, StringBuilder sb, MarkdownRenderContext? context)
    {
        if (table.CellRows.Count == 0)
        {
            sb.AppendLine("*[空表格]*");
            sb.AppendLine();
            return;
        }

        if (table.CellRows.All(row => row.Count == 0))
        {
            sb.AppendLine("*[空表格]*");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("<table style=\"border-collapse:collapse;border-spacing:0;margin:12px 0;\">");
        foreach (var row in table.CellRows)
        {
            sb.AppendLine("    <tr>");
            foreach (var cell in row)
            {
                var spanAttributes = BuildTableCellSpanAttributes(cell);
                var style = BuildTableCellStyle(cell);
                var cellHtml = RenderTableCellHtml(cell, context);
                sb.AppendLine($"      <td{spanAttributes} style=\"{style}\">{cellHtml}</td>");
            }
            sb.AppendLine("    </tr>");
        }
        sb.AppendLine("</table>");
        sb.AppendLine();
    }

    private static string BuildTableCellSpanAttributes(TableCellBlock cell)
    {
        var sb = new StringBuilder();
        if (cell.RowSpan > 1)
        {
            sb.Append($" rowspan=\"{cell.RowSpan}\"");
        }

        if (cell.ColSpan > 1)
        {
            sb.Append($" colspan=\"{cell.ColSpan}\"");
        }

        return sb.ToString();
    }

    private static string BuildTableCellStyle(TableCellBlock cell)
    {
        var styles = new List<string>
        {
            "border:1px solid #b7b7b7",
            "padding:4px 6px",
            "vertical-align:top",
            "white-space:pre-wrap"
        };

        var backgroundColor = SanitizeCssValue(cell.BackgroundColor);
        if (!string.IsNullOrWhiteSpace(backgroundColor))
        {
            styles.Add($"background-color:{backgroundColor}");
        }

        if (!string.IsNullOrWhiteSpace(cell.HorizontalAlign))
        {
            styles.Add($"text-align:{cell.HorizontalAlign}");
        }

        if (!string.IsNullOrWhiteSpace(cell.VerticalAlign))
        {
            styles.Add($"vertical-align:{cell.VerticalAlign}");
        }

        return string.Join(";", styles) + ";";
    }

    private string RenderTableCellHtml(TableCellBlock cell, MarkdownRenderContext? context)
    {
        if (cell.Runs.Count == 0)
        {
            return "&nbsp;";
        }

        var output = new StringBuilder();
        foreach (var run in cell.Runs)
        {
            if (string.IsNullOrEmpty(run.Text))
            {
                continue;
            }

            if (TryRenderRunImageAsHtml(run.Text, context, out var imageHtml))
            {
                output.Append(imageHtml);
                continue;
            }

            output.Append(RenderTableTextRunHtml(run));
        }

        if (output.Length == 0)
        {
            return "&nbsp;";
        }

        return output
            .ToString()
            .Replace("\r\n", "<br/>", StringComparison.Ordinal)
            .Replace("\n", "<br/>", StringComparison.Ordinal)
            .Replace("\r", "<br/>", StringComparison.Ordinal);
    }

    private bool TryRenderRunImageAsHtml(string runText, MarkdownRenderContext? context, out string imageHtml)
    {
        imageHtml = string.Empty;
        if (!TryParseMarkdownImage(runText, out var altText, out _))
        {
            return false;
        }

        var normalizedMarkdown = runText;
        _ = TryRewriteDataUriImageMarkdown(runText, context, out normalizedMarkdown);
        if (!TryParseMarkdownImage(normalizedMarkdown, out altText, out var imageTarget))
        {
            return false;
        }

        var decodedAlt = UnescapeMarkdownAltText(altText);
        imageHtml = $"<img src=\"{WebUtility.HtmlEncode(imageTarget)}\" alt=\"{WebUtility.HtmlEncode(decodedAlt)}\" style=\"max-width:100%;height:auto;display:block;\" />";
        return true;
    }

    private static bool TryParseMarkdownImage(string markdownImage, out string altText, out string target)
    {
        altText = string.Empty;
        target = string.Empty;

        if (!markdownImage.StartsWith("![", StringComparison.Ordinal) || !markdownImage.EndsWith(')'))
        {
            return false;
        }

        var markerIndex = markdownImage.IndexOf("](", StringComparison.Ordinal);
        if (markerIndex <= 1 || markerIndex >= markdownImage.Length - 2)
        {
            return false;
        }

        altText = markdownImage.Substring(2, markerIndex - 2);
        target = markdownImage.Substring(markerIndex + 2, markdownImage.Length - markerIndex - 3);
        return !string.IsNullOrWhiteSpace(target);
    }

    private static string RenderTableTextRunHtml(TextRun run)
    {
        var content = WebUtility.HtmlEncode(run.Text);
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        if (run.Style.Code)
        {
            content = $"<code>{content}</code>";
        }

        if (run.Style.Bold)
        {
            content = $"<strong>{content}</strong>";
        }

        if (run.Style.Italic)
        {
            content = $"<em>{content}</em>";
        }

        if (run.Style.Underline)
        {
            content = $"<u>{content}</u>";
        }

        if (run.Style.Strikethrough)
        {
            content = $"<s>{content}</s>";
        }

        var inlineStyle = BuildTableTextInlineStyle(run.Style);
        if (!string.IsNullOrWhiteSpace(inlineStyle))
        {
            content = $"<span style=\"{inlineStyle}\">{content}</span>";
        }

        if (!string.IsNullOrWhiteSpace(run.Link))
        {
            content = $"<a href=\"{WebUtility.HtmlEncode(run.Link)}\">{content}</a>";
        }

        return content;
    }

    private static string BuildTableTextInlineStyle(TextStyleStyle style)
    {
        var styles = new List<string>();

        var foregroundColor = SanitizeCssValue(style.ForegroundColor);
        if (!string.IsNullOrWhiteSpace(foregroundColor))
        {
            styles.Add($"color:{foregroundColor}");
        }

        var backgroundColor = SanitizeCssValue(style.BackgroundColor);
        if (!string.IsNullOrWhiteSpace(backgroundColor))
        {
            styles.Add($"background-color:{backgroundColor}");
        }

        var fontSize = SanitizeCssValue(style.FontSize);
        if (!string.IsNullOrWhiteSpace(fontSize))
        {
            styles.Add($"font-size:{fontSize}");
        }

        var fontFamily = SanitizeCssValue(style.FontFamily);
        if (!string.IsNullOrWhiteSpace(fontFamily))
        {
            styles.Add($"font-family:{fontFamily}");
        }

        var lineHeight = SanitizeCssValue(style.LineHeight);
        if (!string.IsNullOrWhiteSpace(lineHeight))
        {
            styles.Add($"line-height:{lineHeight}");
        }

        var letterSpacing = SanitizeCssValue(style.LetterSpacing);
        if (!string.IsNullOrWhiteSpace(letterSpacing))
        {
            styles.Add($"letter-spacing:{letterSpacing}");
        }

        return styles.Count == 0 ? string.Empty : string.Join(";", styles) + ";";
    }

    private static string UnescapeMarkdownAltText(string text)
    {
        return text
            .Replace("\\|", "|", StringComparison.Ordinal)
            .Replace("\\[", "[", StringComparison.Ordinal)
            .Replace("\\]", "]", StringComparison.Ordinal);
    }

    private static string? SanitizeCssValue(string? cssValue)
    {
        if (string.IsNullOrWhiteSpace(cssValue))
        {
            return null;
        }

        var trimmed = cssValue.Trim();
        if (trimmed.IndexOfAny([';', '"', '\'', '<', '>']) >= 0)
        {
            return null;
        }

        return trimmed;
    }

    private void ProcessImageBlock(ImageBlock image, StringBuilder sb, MarkdownRenderContext? context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(image.DataUri))
            {
                sb.AppendLine("*[图片: 无数据]*");
                sb.AppendLine();
                return;
            }

            // 移除 data URI 中的换行符，确保 Markdown 格式正确。
            var cleanDataUri = image.DataUri.Replace("\r", "").Replace("\n", "");

            var imageLinkTarget = cleanDataUri;
            if (context is not null && TryPersistImage(cleanDataUri, context, out var relativePath))
            {
                imageLinkTarget = EscapeMarkdownLinkDestination(relativePath);
            }

            var altText = !string.IsNullOrWhiteSpace(image.Caption) ? image.Caption : "图片";
            
            // Obsidian 支持在 alt text 中指定尺寸。
            if (image.Width > 0 && image.Height > 0)
            {
                sb.AppendLine($"![{altText}|{image.Width}x{image.Height}]({imageLinkTarget})");
            }
            else
            {
                sb.AppendLine($"![{altText}]({imageLinkTarget})");
            }

            if (!string.IsNullOrWhiteSpace(image.Caption))
            {
                sb.AppendLine($"*{image.Caption}*");
            }
            
            sb.AppendLine();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"处理图片块失败: {ex.Message}");
            sb.AppendLine($"*[图片: 处理失败 - {ex.Message}]*");
            sb.AppendLine();
        }
    }

    private static bool TryPersistImage(string dataUri, MarkdownRenderContext context, out string relativePath)
    {
        relativePath = string.Empty;
        if (!TryParseDataUri(dataUri, out var mimeType, out var base64Data))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Data);
        }
        catch (FormatException)
        {
            return false;
        }

        var extension = ResolveImageExtension(mimeType);
        var fileName = $"image-{context.NextImageIndex():D4}.{extension}";
        Directory.CreateDirectory(context.ImageDirectoryPath);

        var absolutePath = Path.Combine(context.ImageDirectoryPath, fileName);
        File.WriteAllBytes(absolutePath, bytes);

        relativePath = $"{context.ImageDirectoryName}/{fileName}".Replace('\\', '/');
        return true;
    }

    private static bool TryParseDataUri(string dataUri, out string mimeType, out string base64Data)
    {
        mimeType = string.Empty;
        base64Data = string.Empty;

        if (!dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = dataUri.IndexOf(',');
        if (commaIndex <= 5 || commaIndex >= dataUri.Length - 1)
        {
            return false;
        }

        var header = dataUri.Substring(5, commaIndex - 5);
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separatorIndex = header.IndexOf(';');
        if (separatorIndex <= 0)
        {
            return false;
        }

        mimeType = header.Substring(0, separatorIndex);
        base64Data = dataUri.Substring(commaIndex + 1);
        return !string.IsNullOrWhiteSpace(mimeType) && !string.IsNullOrWhiteSpace(base64Data);
    }

    private static string ResolveImageExtension(string mimeType)
    {
        return mimeType.ToLowerInvariant() switch
        {
            "image/jpeg" => "jpg",
            "image/jpg" => "jpg",
            "image/png" => "png",
            "image/gif" => "gif",
            "image/bmp" => "bmp",
            "image/tiff" => "tiff",
            "image/webp" => "webp",
            "image/svg+xml" => "svg",
            _ => "img"
        };
    }

    private static string ResolveAttachmentExtension(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            return "bin";
        }

        return mimeType.ToLowerInvariant() switch
        {
            "application/pdf" => "pdf",
            "application/msword" => "doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
            "application/vnd.ms-excel" => "xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "xlsx",
            "application/vnd.ms-powerpoint" => "ppt",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "pptx",
            "text/plain" => "txt",
            "application/zip" => "zip",
            "application/x-zip-compressed" => "zip",
            "image/jpeg" => "jpg",
            "image/png" => "png",
            _ => "bin"
        };
    }

    private void ProcessAttachmentBlock(AttachmentBlock attachment, StringBuilder sb, MarkdownRenderContext? context)
    {
        try
        {
            if (context is not null && TryPersistAttachment(attachment, context, out var relativePath, out var bytesWritten))
            {
                var fileSize = attachment.FileSize > 0 ? attachment.FileSize : bytesWritten;
                sb.AppendLine($"[附件] [{attachment.FileName}]({EscapeMarkdownLinkDestination(relativePath)}) ({FormatFileSize(fileSize)})");
                sb.AppendLine();
                return;
            }

            string? base64Data = attachment.FileDataBase64;
            var mimeType = string.IsNullOrWhiteSpace(attachment.MimeType) ? "application/octet-stream" : attachment.MimeType;

            if (string.IsNullOrWhiteSpace(base64Data)
                && !string.IsNullOrWhiteSpace(attachment.PathCache)
                && File.Exists(attachment.PathCache))
            {
                try
                {
                    var fileBytes = File.ReadAllBytes(attachment.PathCache);
                    base64Data = Convert.ToBase64String(fileBytes);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn($"读取附件文件失败: {attachment.FileName}, 错误: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(base64Data))
            {
                var dataUri = $"data:{mimeType};base64,{base64Data}";
                var rawBytes = attachment.FileSize > 0 ? attachment.FileSize : (long)Math.Floor(base64Data.Length * 0.75);
                sb.AppendLine($"[附件] [{attachment.FileName}]({dataUri}) ({FormatFileSize(rawBytes)})");
            }
            else
            {
                sb.AppendLine($"> 附件无法嵌入: {attachment.FileName}");
                if (!string.IsNullOrWhiteSpace(attachment.ErrorMessage))
                {
                    sb.AppendLine($"> 错误: {attachment.ErrorMessage}");
                }
            }

            sb.AppendLine();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"处理附件块失败: {ex.Message}");
            sb.AppendLine($"> 附件处理失败: {attachment.FileName} - {ex.Message}");
            sb.AppendLine();
        }
    }

    private static bool TryPersistAttachment(
        AttachmentBlock attachment,
        MarkdownRenderContext context,
        out string relativePath,
        out long bytesWritten)
    {
        relativePath = string.Empty;
        bytesWritten = 0;

        var fileName = BuildAttachmentFileName(attachment, context.NextAttachmentIndex());
        Directory.CreateDirectory(context.AttachmentDirectoryPath);

        var absolutePath = Path.Combine(context.AttachmentDirectoryPath, fileName);
        if (TryCopyAttachmentFromPathCache(attachment, absolutePath))
        {
            bytesWritten = new FileInfo(absolutePath).Length;
            relativePath = $"{context.AttachmentDirectoryName}/{fileName}".Replace('\\', '/');
            return true;
        }

        if (TryWriteAttachmentFromBase64(attachment, absolutePath, out bytesWritten))
        {
            relativePath = $"{context.AttachmentDirectoryName}/{fileName}".Replace('\\', '/');
            return true;
        }

        return false;
    }

    private static bool TryCopyAttachmentFromPathCache(AttachmentBlock attachment, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(attachment.PathCache) || !File.Exists(attachment.PathCache))
        {
            return false;
        }

        try
        {
            File.Copy(attachment.PathCache, outputPath, overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"从 pathCache 复制附件失败: {attachment.FileName}, 错误: {ex.Message}");
            return false;
        }
    }

    private static bool TryWriteAttachmentFromBase64(AttachmentBlock attachment, string outputPath, out long bytesWritten)
    {
        bytesWritten = 0;
        if (string.IsNullOrWhiteSpace(attachment.FileDataBase64))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(attachment.FileDataBase64);
            File.WriteAllBytes(outputPath, bytes);
            bytesWritten = bytes.LongLength;
            return true;
        }
        catch (Exception ex) when (ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn($"从 base64 写入附件失败: {attachment.FileName}, 错误: {ex.Message}");
            return false;
        }
    }

    private static string BuildAttachmentFileName(AttachmentBlock attachment, int index)
    {
        var sourceName = string.IsNullOrWhiteSpace(attachment.FileName)
            ? "attachment"
            : attachment.FileName;
        var sanitized = SanitizeFileName(sourceName);

        if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitized)))
        {
            sanitized = $"{sanitized}.{ResolveAttachmentExtension(attachment.MimeType)}";
        }

        return $"attachment-{index:D4}-{sanitized}";
    }

    private void ProcessUnsupportedBlock(UnsupportedBlock unsupported, StringBuilder sb)
    {
        sb.AppendLine($"> 不支持的元素 ({unsupported.Kind}): {unsupported.DegradeTo}");
        sb.AppendLine();
    }

    private string RenderTextRuns(IReadOnlyList<TextRun> runs, MarkdownRenderContext? context)
    {
        if (runs == null || runs.Count == 0)
        {
            return "";
        }

        var sb = new StringBuilder();
        var highlightOpen = false;
        foreach (var run in runs)
        {
            var text = run.Text;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            // Keep pre-built markdown images (data URI) unescaped, otherwise they become plain text.
            if (IsDataUriImageMarkdown(text))
            {
                if (highlightOpen)
                {
                    sb.Append("==");
                    highlightOpen = false;
                }

                if (TryRewriteDataUriImageMarkdown(text, context, out var rewrittenImageMarkdown))
                {
                    sb.Append(rewrittenImageMarkdown);
                }
                else
                {
                    sb.Append(text);
                }
                continue;
            }

            if (ShouldRenderRunAsHtml(run))
            {
                if (highlightOpen)
                {
                    sb.Append("==");
                    highlightOpen = false;
                }

                sb.Append(RenderTableTextRunHtml(run));
                continue;
            }

            // 转义特殊字符
            text = text.Replace("\\", "\\\\")
                       .Replace("*", "\\*")
                       .Replace("_", "\\_")
                       .Replace("[", "\\[")
                       .Replace("]", "\\]")
                       .Replace("`", "\\`")
                       .Replace("<", "&lt;")
                       .Replace(">", "&gt;");

            // 应用样式
            if (run.Style.Code) text = $"`{text}`";
            if (run.Style.Strikethrough) text = $"~~{text}~~";
            if (run.Style.Italic) text = $"*{text}*";
            if (run.Style.Bold) text = $"**{text}**";

            // 链接
            if (!string.IsNullOrWhiteSpace(run.Link))
            {
                text = $"[{text}]({run.Link})";
            }

            // Merge adjacent highlighted runs into a single ==...== segment.
            var shouldHighlight = ShouldApplyMarkdownHighlight(run.Style.BackgroundColor);
            if (shouldHighlight && !highlightOpen)
            {
                sb.Append("==");
                highlightOpen = true;
            }
            else if (!shouldHighlight && highlightOpen)
            {
                sb.Append("==");
                highlightOpen = false;
            }

            sb.Append(text);
        }

        if (highlightOpen)
        {
            sb.Append("==");
        }

        var result = sb.ToString();
        // Preserve explicit line breaks from OneNote content.
        result = result.Replace("\r\n", "<br/>", StringComparison.Ordinal)
                       .Replace("\n", "<br/>", StringComparison.Ordinal)
                       .Replace("\r", "<br/>", StringComparison.Ordinal);

        return result;
    }

    private static bool ShouldRenderRunAsHtml(TextRun run)
    {
        return !string.IsNullOrWhiteSpace(SanitizeCssValue(run.Style.ForegroundColor))
               || !string.IsNullOrWhiteSpace(SanitizeCssValue(run.Style.BackgroundColor))
               || !string.IsNullOrWhiteSpace(SanitizeCssValue(run.Style.FontSize))
               || !string.IsNullOrWhiteSpace(SanitizeCssValue(run.Style.FontFamily))
               || !string.IsNullOrWhiteSpace(SanitizeCssValue(run.Style.LineHeight))
               || !string.IsNullOrWhiteSpace(SanitizeCssValue(run.Style.LetterSpacing));
    }

    private static void PrepareOutputDirectory(string outputPath)
    {
        Directory.CreateDirectory(outputPath);

        foreach (var entry in Directory.EnumerateFileSystemEntries(outputPath))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // Keep system/workspace metadata directories such as ".obsidian".
            if (name.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }
        }
    }

    private static bool TryRewriteDataUriImageMarkdown(
        string markdownImage,
        MarkdownRenderContext? context,
        out string rewrittenMarkdownImage)
    {
        rewrittenMarkdownImage = markdownImage;
        if (context is null || !IsDataUriImageMarkdown(markdownImage))
        {
            return false;
        }

        var markerIndex = markdownImage.IndexOf("](", StringComparison.Ordinal);
        var altText = markdownImage.Substring(2, markerIndex - 2);
        var uri = markdownImage.Substring(markerIndex + 2, markdownImage.Length - markerIndex - 3);

        if (!TryPersistImage(uri, context, out var relativePath))
        {
            return false;
        }

        // Keep table cells stable by escaping unescaped pipes in alt text.
        var normalizedAltText = Regex.Replace(altText, @"(?<!\\)\|", "\\|");
        rewrittenMarkdownImage = $"![{normalizedAltText}]({EscapeMarkdownLinkDestination(relativePath)})";
        return true;
    }

    private static string RepairAndValidateTableMarkup(string markdown, string pageIdentity)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains("<table", StringComparison.OrdinalIgnoreCase))
        {
            return markdown;
        }

        var before = ValidateHtmlTagBalanceInTables(markdown);
        var repaired = RepairCorruptedClosersInTables(markdown);
        var after = ValidateHtmlTagBalanceInTables(repaired);

        if (before.HasIssues || after.HasIssues)
        {
            DiagnosticLogger.Warn(
                $"表格HTML完整性检查: page={pageIdentity}, before={before}, after={after}");
        }

        return repaired;
    }

    private static string RepairCorruptedClosersInTables(string markdown)
    {
        return TableSegmentRegex.Replace(markdown, match =>
            CorruptedCloserRegex.Replace(match.Value, "</$1>"));
    }

    private static HtmlTagBalanceSummary ValidateHtmlTagBalanceInTables(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return new HtmlTagBalanceSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var tableSegments = TableSegmentRegex.Matches(markdown);
        if (tableSegments.Count == 0)
        {
            return new HtmlTagBalanceSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        var segmentText = string.Join("\n", tableSegments.Select(m => m.Value));
        return new HtmlTagBalanceSummary(
            TableOpen: CountMatches(segmentText, @"<table\b"),
            TableClose: CountMatches(segmentText, @"</table>"),
            TrOpen: CountMatches(segmentText, @"<tr\b"),
            TrClose: CountMatches(segmentText, @"</tr>"),
            TdOpen: CountMatches(segmentText, @"<td\b"),
            TdClose: CountMatches(segmentText, @"</td>"),
            SpanOpen: CountMatches(segmentText, @"<span\b"),
            SpanClose: CountMatches(segmentText, @"</span>"),
            StrongOpen: CountMatches(segmentText, @"<strong\b"),
            StrongClose: CountMatches(segmentText, @"</strong>"));
    }

    private static int CountMatches(string input, string pattern)
    {
        return Regex.Matches(input, pattern, RegexOptions.IgnoreCase).Count;
    }

    private static bool IsDataUriImageMarkdown(string text)
    {
        if (!text.StartsWith("![", StringComparison.Ordinal) || !text.EndsWith(")", StringComparison.Ordinal))
        {
            return false;
        }

        var markerIndex = text.IndexOf("](", StringComparison.Ordinal);
        if (markerIndex <= 1 || markerIndex >= text.Length - 2)
        {
            return false;
        }

        var uri = text.Substring(markerIndex + 2, text.Length - markerIndex - 3);
        return uri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static bool ShouldApplyMarkdownHighlight(string? backgroundColor)
    {
        if (string.IsNullOrWhiteSpace(backgroundColor))
        {
            return false;
        }

        var normalized = backgroundColor.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"\s+", string.Empty);

        return normalized switch
        {
            "automatic" => false,
            "transparent" => false,
            "none" => false,
            "inherit" => false,
            "initial" => false,
            "unset" => false,
            "white" => false,
            "#fff" => false,
            "#ffffff" => false,
            "rgb(255,255,255)" => false,
            "rgba(255,255,255,1)" => false,
            _ => true
        };
    }

    private static string EscapeMarkdownLinkDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return destination;
        }

        if (destination.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }

        var normalized = destination.Replace('\\', '/');
        var segments = normalized.Split('/');
        var escapedSegments = segments.Select(Uri.EscapeDataString);
        return string.Join("/", escapedSegments);
    }

    private static string CombineOriginalPath(string? parentPath, string nodeName)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            return nodeName;
        }

        return $"{parentPath}/{nodeName}";
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Untitled";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized.Substring(0, 100);
        }

        return sanitized.Trim();
    }

    private static string EscapeYaml(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }
        
        if (text.Contains(':') || text.Contains('"') || text.Contains('\'') || 
            text.Contains('[') || text.Contains(']') || text.Contains('{') || text.Contains('}') ||
            text.Contains('\n') || text.Contains('\r') || text.Contains('#'))
        {
            return $"\"{text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")}\"";
        }

        return text;
    }

    public static int CountAllNodes(IEnumerable<OneNoteTreeNode> nodes)
    {
        return nodes.Sum(CountNodes);
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
}

public sealed class ExportProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentNodeName { get; set; } = "";
    public string Status { get; set; } = "";
}

public sealed class ExportResult
{
    public int ExportedFiles { get; set; }
    public List<FailedExportItem> FailedNodes { get; } = new();
    public bool WasCanceled { get; set; }
    public bool IsSuccess => FailedNodes.Count == 0 && !WasCanceled;
}

public sealed class FailedExportItem
{
    public OneNoteTreeNode Node { get; set; } = null!;
    public string Error { get; set; } = "";
}

internal sealed class ExportCounter
{
    public int Total { get; set; }
    private int _current;
    public int Current => _current;
    public int Increment() => Interlocked.Increment(ref _current);
}

internal sealed class MarkdownRenderContext
{
    private int _imageIndex;
    private int _attachmentIndex;

    public MarkdownRenderContext(string folderPath, string imageDirectoryName, string attachmentDirectoryName)
    {
        FolderPath = folderPath;
        ImageDirectoryName = imageDirectoryName;
        ImageDirectoryPath = Path.Combine(folderPath, imageDirectoryName);
        AttachmentDirectoryName = attachmentDirectoryName;
        AttachmentDirectoryPath = Path.Combine(folderPath, attachmentDirectoryName);
    }

    public string FolderPath { get; }
    public string ImageDirectoryName { get; }
    public string ImageDirectoryPath { get; }
    public string AttachmentDirectoryName { get; }
    public string AttachmentDirectoryPath { get; }

    public int NextImageIndex() => Interlocked.Increment(ref _imageIndex);
    public int NextAttachmentIndex() => Interlocked.Increment(ref _attachmentIndex);
}

internal readonly record struct HtmlTagBalanceSummary(
    int TableOpen,
    int TableClose,
    int TrOpen,
    int TrClose,
    int TdOpen,
    int TdClose,
    int SpanOpen,
    int SpanClose,
    int StrongOpen,
    int StrongClose)
{
    public bool HasIssues =>
        TableOpen != TableClose
        || TrOpen != TrClose
        || TdOpen != TdClose
        || SpanOpen != SpanClose
        || StrongOpen != StrongClose;

    public override string ToString()
    {
        return $"table={TableOpen}/{TableClose}, tr={TrOpen}/{TrClose}, td={TdOpen}/{TdClose}, span={SpanOpen}/{SpanClose}, strong={StrongOpen}/{StrongClose}, hasIssues={HasIssues}";
    }
}






