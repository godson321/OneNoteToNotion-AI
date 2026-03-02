using System.Text;
using System.Text.RegularExpressions;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Mapping;
using OneNoteToNotion.Notion;

namespace OneNoteToNotion.Infrastructure;

public sealed class NotionBlockMapper : INotionBlockMapper
{
    private readonly NotionStyleMappingRules _rules = new();

    public IReadOnlyList<NotionBlockInput> Map(SemanticDocument semanticDocument)
    {
        var blocks = new List<NotionBlockInput>();
        var indentStack = new List<NotionBlockInput?>();

        static bool SupportsChildren(string type)
        {
            return type is "paragraph"
                   or "bulleted_list_item"
                   or "numbered_list_item"
                   or "to_do"
                   or "toggle"
                   or "quote"
                   or "callout"
                   or "synced_block"
                   or "template";
        }

        void AddBlockWithIndent(NotionBlockInput block, int indentLevel)
        {
            if (indentLevel <= 0 || indentStack.Count == 0)
            {
                blocks.Add(block);
                indentStack.Clear();
                indentStack.Add(block);
                return;
            }

            var parentDepth = Math.Min(indentLevel - 1, indentStack.Count - 1);
            while (parentDepth >= 0)
            {
                var candidate = indentStack[parentDepth];
                if (candidate is not null && SupportsChildren(candidate.Type))
                {
                    candidate.Children.Add(block);
                    break;
                }

                parentDepth--;
            }

            if (parentDepth < 0)
            {
                blocks.Add(block);
            }

            if (indentStack.Count > indentLevel)
            {
                indentStack[indentLevel] = block;
                if (indentStack.Count > indentLevel + 1)
                {
                    indentStack.RemoveRange(indentLevel + 1, indentStack.Count - (indentLevel + 1));
                }
            }
            else
            {
                while (indentStack.Count < indentLevel)
                {
                    indentStack.Add(null);
                }

                indentStack.Add(block);
            }
        }

        foreach (var block in semanticDocument.Blocks)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var headingRuns = heading.Runs ?? [new TextRun(heading.Text, new TextStyleStyle())];
                    AddBlockWithIndent(new NotionBlockInput
                    {
                        Type = heading.Level <= 1 ? "heading_1" : heading.Level == 2 ? "heading_2" : "heading_3",
                        Value = new { rich_text = BuildRichText(headingRuns) }
                    }, 0);
                    break;
                case ParagraphBlock paragraph:
                    var paragraphIndent = Math.Max(0, paragraph.IndentLevel);
                    foreach (var fallback in BuildFallbackForParagraph(paragraph))
                    {
                        AddBlockWithIndent(fallback, paragraphIndent);
                    }

                    AddBlockWithIndent(new NotionBlockInput
                    {
                        Type = "paragraph",
                        Value = new { rich_text = BuildRichText(paragraph.Runs) }
                    }, paragraphIndent);
                    break;
                case TableBlock table:
                    foreach (var tableBlock in BuildTableBlocks(table))
                    {
                        AddBlockWithIndent(tableBlock, 0);
                    }
                    break;
                case BulletedListBlock bulleted:
                    foreach (var itemRuns in bulleted.Items)
                    {
                        AddBlockWithIndent(new NotionBlockInput
                        {
                            Type = "bulleted_list_item",
                            Value = new { rich_text = BuildRichText(itemRuns) }
                        }, 0);
                    }
                    break;
                case NumberedListBlock numbered:
                    foreach (var itemRuns in numbered.Items)
                    {
                        AddBlockWithIndent(new NotionBlockInput
                        {
                            Type = "numbered_list_item",
                            Value = new { rich_text = BuildRichText(itemRuns) }
                        }, 0);
                    }
                    break;
                case ImageBlock image:
                {
                    if (!string.IsNullOrWhiteSpace(image.ProcessingError))
                    {
                        DiagnosticLogger.Warn(
                            $"图片处理告警: format={image.OriginalFormat}, originalSize={image.OriginalSize}, finalSize={image.FinalSize}, detail={image.ProcessingError}");
                    }

                    if (!string.IsNullOrWhiteSpace(image.SyncError))
                    {
                        DiagnosticLogger.Warn(
                            $"图片同步告警: format={image.OriginalFormat}, size={image.FinalSize}, detail={image.SyncError}");
                        AddBlockWithIndent(BuildFallbackParagraph($"[图片] {image.SyncError}"), 0);
                        break;
                    }

                    if (!string.IsNullOrWhiteSpace(image.NotionFileId))
                    {
                        AddBlockWithIndent(new NotionBlockInput
                        {
                            Type = "image",
                            Value = new
                            {
                                type = "file_upload",
                                file_upload = new { id = image.NotionFileId }
                            }
                        }, 0);
                    }
                    else
                    {
                        DiagnosticLogger.Warn($"图片未上传，降级处理: format={image.OriginalFormat}, size={image.FinalSize}");
                        AddBlockWithIndent(BuildFallbackParagraph("[图片] 未成功上传"), 0);
                    }
                    break;
                }
                case AttachmentBlock attachment:
                {
                    // 如果有错误信息，输出警告日志并降级
                    if (!string.IsNullOrWhiteSpace(attachment.ErrorMessage))
                    {
                        DiagnosticLogger.Warn(
                            $"附件同步告警: FileName={attachment.FileName}, Size={attachment.FileSize}, Error={attachment.ErrorMessage}");
                        AddBlockWithIndent(BuildFallbackParagraph($"[附件: {attachment.FileName}] {attachment.ErrorMessage}"), 0);
                        break;
                    }

                    // 检查是否已上传（由 NotionSyncOrchestrator 预处理）
                    if (!string.IsNullOrWhiteSpace(attachment.NotionFileName))
                    {
                        // 使用 file_upload 引用 Notion 文件对象，避免拼接无效 external URL
                        AddBlockWithIndent(new NotionBlockInput
                        {
                            Type = "file",
                            Value = new
                            {
                                type = "file_upload",
                                file_upload = new { id = attachment.NotionFileName }
                            }
                        }, 0);
                    }
                    else
                    {
                        // 未上传，降级为占位符
                        DiagnosticLogger.Warn($"附件未上传，降级处理: {attachment.FileName}");
                        AddBlockWithIndent(BuildFallbackParagraph($"[附件: {attachment.FileName}] 未成功上传"), 0);
                    }
                    break;
                }
                case UnsupportedBlock unsupported:
                    AddBlockWithIndent(BuildFallbackParagraph($"[{unsupported.Kind}] {unsupported.DegradeTo}"), 0);
                    break;
                default:
                    AddBlockWithIndent(BuildFallbackParagraph($"[Unsupported block] {block.GetType().Name}"), 0);
                    break;
            }
        }

        return blocks;
    }

    private IReadOnlyList<NotionBlockInput> BuildFallbackForParagraph(ParagraphBlock paragraph)
    {
        var blocks = new List<NotionBlockInput>();
        var alignmentFallback = _rules.BuildAlignmentFallbackMessage(paragraph.Alignment);
        if (!string.IsNullOrWhiteSpace(alignmentFallback))
        {
            blocks.Add(BuildFallbackParagraph(alignmentFallback));
        }

        var layoutFallback = _rules.BuildLayoutFallbackMessage(paragraph.Layout);
        if (!string.IsNullOrWhiteSpace(layoutFallback))
        {
            blocks.Add(BuildFallbackParagraph(layoutFallback));
        }

        return blocks;
    }

    private const int NotionMaxTableRows = 100;

    private IEnumerable<NotionBlockInput> BuildTableBlocks(TableBlock table)
    {
        if (table.Rows.Count <= NotionMaxTableRows)
        {
            yield return BuildSingleTableBlock(table.Rows, 0, table.Rows.Count, hasHeader: table.Rows.Count > 1);
            yield break;
        }

        // Split large table: first chunk = header + 99 data rows, subsequent chunks = header + 99 data rows
        var headerRow = table.Rows[0];
        var dataRows = table.Rows.Skip(1).ToList();
        var chunkSize = NotionMaxTableRows - 1; // Reserve 1 row for header

        for (var i = 0; i < dataRows.Count; i += chunkSize)
        {
            var chunkRows = new List<IReadOnlyList<IReadOnlyList<TextRun>>> { headerRow };
            chunkRows.AddRange(dataRows.Skip(i).Take(chunkSize));

            yield return BuildSingleTableBlock(chunkRows, 0, chunkRows.Count, hasHeader: true);
        }
    }

    private NotionBlockInput BuildSingleTableBlock(
        IReadOnlyList<IReadOnlyList<IReadOnlyList<TextRun>>> rows,
        int startIndex, int count, bool hasHeader)
    {
        var tableWidth = rows.Max(r => r.Count);
        var children = rows
            .Skip(startIndex)
            .Take(count)
            .Select((row, rowIndex) => new
            {
                type = "table_row",
                table_row = new
                {
                    cells = row
                        .Select(cellRuns =>
                        {
                            var runs = cellRuns as IReadOnlyList<TextRun> ?? [new TextRun(string.Empty, new TextStyleStyle())];

                            // Force bold for header row (first row)
                            if (rowIndex == 0 && hasHeader)
                            {
                                runs = runs.Select(r => r with
                                {
                                    Style = r.Style with { Bold = true }
                                }).ToList();
                            }

                            return BuildRichText(runs);
                        })
                        .ToList()
                }
            })
            .ToList();

        return new NotionBlockInput
        {
            Type = "table",
            Value = new
            {
                table_width = tableWidth,
                has_column_header = hasHeader,
                has_row_header = false,
                children
            }
        };
    }

    private const int NotionMaxTextLength = 2000;

    private object[] BuildRichText(IReadOnlyList<TextRun> runs)
    {
        if (runs.Count == 0)
        {
            runs = [new TextRun(string.Empty, new TextStyleStyle())];
        }

        return SplitLongRuns(runs)
            .Select(run => (object)new
            {
                type = "text",
                text = new
                {
                    content = PreserveIndentationForNotion(run.Text),
                    link = run.Link is null ? null : new { url = run.Link }
                },
                annotations = new
                {
                    bold = run.Style.Bold,
                    italic = run.Style.Italic,
                    underline = run.Style.Underline,
                    strikethrough = run.Style.Strikethrough,
                    code = run.Style.Code,
                    color = ResolveColor(run.Style)
                }
            })
            .ToArray();
    }

    private static string PreserveIndentationForNotion(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var builder = new StringBuilder(normalized.Length);
        var atLineStart = true;

        foreach (var ch in normalized)
        {
            switch (ch)
            {
                case '\n':
                    builder.Append('\n');
                    atLineStart = true;
                    break;
                case ' ' when atLineStart:
                    builder.Append('\u00A0');
                    break;
                case '\t' when atLineStart:
                    builder.Append("\u00A0\u00A0\u00A0\u00A0");
                    break;
                default:
                    builder.Append(ch);
                    atLineStart = false;
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Split any TextRun whose text exceeds the Notion 2000-char limit into multiple runs
    /// with the same style, breaking at the last space before the limit when possible.
    /// </summary>
    private static IEnumerable<TextRun> SplitLongRuns(IReadOnlyList<TextRun> runs)
    {
        foreach (var run in runs)
        {
            if (run.Text.Length <= NotionMaxTextLength)
            {
                yield return run;
                continue;
            }

            var remaining = run.Text;
            while (remaining.Length > NotionMaxTextLength)
            {
                var breakAt = remaining.LastIndexOf(' ', NotionMaxTextLength - 1);
                if (breakAt <= 0) breakAt = NotionMaxTextLength;

                yield return new TextRun(remaining[..breakAt], run.Style, run.Link);
                remaining = remaining[breakAt..].TrimStart();
            }

            if (remaining.Length > 0)
            {
                yield return new TextRun(remaining, run.Style, run.Link);
            }
        }
    }

    private string ResolveColor(TextStyleStyle style)
    {
        // Strategy: Convert OneNote background colors to Notion text colors
        // This preserves visual distinction while keeping Notion's clean white background
        
        var hasBg = !string.IsNullOrWhiteSpace(style.BackgroundColor);
        if (hasBg)
        {
            var bgMapped = _rules.MapColor(style.BackgroundColor);
            // If background is meaningful (not white/default), use it as text color
            if (bgMapped != "default")
            {
                return bgMapped;  // Use as text color, not background
            }
        }

        // If no meaningful background, use foreground color
        var hasFg = !string.IsNullOrWhiteSpace(style.ForegroundColor);
        if (hasFg)
        {
            return _rules.MapColor(style.ForegroundColor);
        }

        return "default";
    }

    private static NotionBlockInput BuildFallbackParagraph(string message)
    {
        return new NotionBlockInput
        {
            Type = "paragraph",
            Value = new
            {
                rich_text = new object[]
                {
                    new
                    {
                        type = "text",
                        text = new
                        {
                            content = $"[降级说明] {message}"
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// 解析 Data URI，返回 (base64Data, format)
    /// </summary>
    /// <example>
    /// data:image/png;base64,iVBORw0KG... => ("iVBORw0KG...", "png")
    /// </example>
    private static (string base64Data, string format) ParseDataUri(string dataUri)
    {
        // data:image/png;base64,iVBORw0KG...
        var parts = dataUri.Split(',');
        if (parts.Length < 2) return (string.Empty, "png");

        var header = parts[0]; // data:image/png;base64
        var base64Data = parts[1];

        // 提取格式
        var format = "png";
        var formatMatch = Regex.Match(header, @"image/(\w+)");
        if (formatMatch.Success)
        {
            format = formatMatch.Groups[1].Value.ToLowerInvariant();
        }

        return (base64Data, format);
    }
}
