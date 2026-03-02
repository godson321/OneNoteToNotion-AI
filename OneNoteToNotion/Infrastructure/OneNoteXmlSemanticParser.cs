using System.Text.RegularExpressions;
using System.Xml.Linq;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Parsing;

namespace OneNoteToNotion.Infrastructure;

public sealed class OneNoteXmlSemanticParser : ISemanticDocumentParser
{
    private static readonly XNamespace OneNs = "http://schemas.microsoft.com/office/onenote/2013/onenote";

    public SemanticDocument Parse(string oneNotePageXml)
    {
        var doc = XDocument.Parse(oneNotePageXml);
        var page = doc.Descendants(OneNs + "Page").FirstOrDefault();
        var model = new SemanticDocument
        {
            Title = page?.Attribute("name")?.Value ?? "Untitled"
        };

        // Build heading-style map from QuickStyleDef
        var headingStyles = BuildHeadingStyleMap(page);

        // Process Outline elements in document order
        var outlines = page?.Elements(OneNs + "Outline") ?? Enumerable.Empty<XElement>();
        foreach (var outline in outlines)
        {
            var oeChildren = outline.Element(OneNs + "OEChildren");
            if (oeChildren is not null)
            {
                ProcessOEChildren(oeChildren, model.Blocks, headingStyles, depth: 0);
            }
        }

        return model;
    }

    private static Dictionary<string, (int level, TextStyleStyle style)> BuildHeadingStyleMap(XElement? page)
    {
        var map = new Dictionary<string, (int level, TextStyleStyle style)>(StringComparer.OrdinalIgnoreCase);
        if (page is null) return map;

        foreach (var qsd in page.Elements(OneNs + "QuickStyleDef"))
        {
            var index = qsd.Attribute("index")?.Value;
            var name = qsd.Attribute("name")?.Value;
            if (index is null || name is null) continue;

            // Match "h1"~"h6" or "Heading 1"~"Heading 6"
            var headingMatch = Regex.Match(name, @"^h(\d)$", RegexOptions.IgnoreCase);
            if (!headingMatch.Success)
            {
                headingMatch = Regex.Match(name, @"^heading\s*(\d)$", RegexOptions.IgnoreCase);
            }

            if (headingMatch.Success)
            {
                var level = int.Parse(headingMatch.Groups[1].Value);
                var clampedLevel = Math.Clamp(level, 1, 3);

                // Extract style from QuickStyleDef attributes
                var fontColor = qsd.Attribute("fontColor")?.Value;
                var highlightColor = qsd.Attribute("highlightColor")?.Value;
                var bold = qsd.Attribute("bold")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
                var italic = qsd.Attribute("italic")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
                var underline = qsd.Attribute("underline")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
                var strikethrough = qsd.Attribute("strikethrough")?.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

                // Normalize colors (skip "automatic")
                var fg = fontColor is not null && !fontColor.Equals("automatic", StringComparison.OrdinalIgnoreCase)
                    ? fontColor
                    : null;
                var bg = highlightColor is not null && !highlightColor.Equals("automatic", StringComparison.OrdinalIgnoreCase)
                    ? highlightColor
                    : null;

                var style = new TextStyleStyle(bold, italic, underline, strikethrough, false, fg, bg);
                map[index] = (clampedLevel, style);
            }
        }

        return map;
    }

    private void ProcessOEChildren(
        XElement oeChildren,
        List<SemanticBlock> blocks,
        Dictionary<string, (int level, TextStyleStyle style)> headingStyles,
        int depth)
    {
        var pendingBulletItems = new List<IReadOnlyList<TextRun>>();
        var pendingNumberItems = new List<IReadOnlyList<TextRun>>();

        foreach (var oe in oeChildren.Elements(OneNs + "OE"))
        {
            var listElement = oe.Element(OneNs + "List");
            var isBullet = listElement?.Element(OneNs + "Bullet") is not null;
            var isNumber = listElement?.Element(OneNs + "Number") is not null;

            // Flush pending list items when list type changes
            if (!isBullet && pendingBulletItems.Count > 0)
            {
                blocks.Add(new BulletedListBlock(pendingBulletItems.ToList()));
                pendingBulletItems.Clear();
            }

            if (!isNumber && pendingNumberItems.Count > 0)
            {
                blocks.Add(new NumberedListBlock(pendingNumberItems.ToList()));
                pendingNumberItems.Clear();
            }

            // Check for image
            var imageElement = oe.Element(OneNs + "Image");
            if (imageElement is not null)
            {
                var imageBlock = ParseImage(imageElement, out var imageError);
                if (imageBlock is not null)
                {
                    blocks.Add(imageBlock);
                }
                else
                {
                    // 图片解析失败，降级处理并附带可读原因
                    var degradeMessage = string.IsNullOrWhiteSpace(imageError)
                        ? "图片解析失败"
                        : $"图片处理失败: {imageError}";
                    blocks.Add(new UnsupportedBlock("Image", imageElement.ToString(), degradeMessage));
                }
                continue;
            }

            // Check for inserted file (attachment)
            var insertedFileElement = oe.Element(OneNs + "InsertedFile");
            if (insertedFileElement is not null)
            {
                var attachmentBlock = ParseInsertedFile(insertedFileElement);
                if (attachmentBlock is not null)
                {
                    blocks.Add(attachmentBlock);
                }
                else
                {
                    // 附件解析失败，降级处理
                    blocks.Add(new UnsupportedBlock("InsertedFile", insertedFileElement.ToString(), "附件解析失败"));
                }
                continue;
            }

            // Check for table
            var tableElement = oe.Element(OneNs + "Table");
            if (tableElement is not null)
            {
                var tableBlock = ParseTable(tableElement);
                if (tableBlock is not null)
                {
                    blocks.Add(tableBlock);
                }

                // OneNote 表格单元格可能包含图片（Notion 表格不支持单元格图片），
                // 这里提取为普通图片块，避免“丢图”。
                var tableImageBlocks = ParseImagesFromTable(tableElement);
                if (tableImageBlocks.Count > 0)
                {
                    blocks.AddRange(tableImageBlocks);
                }

                continue;
            }

            // Collect text from all <T> elements in this OE
            var textContent = CollectOEText(oe, depth);

            if (isBullet)
            {
                if (textContent is ParagraphBlock pb)
                {
                    pendingBulletItems.Add(pb.Runs);
                }

                continue;
            }

            if (isNumber)
            {
                if (textContent is ParagraphBlock pb)
                {
                    pendingNumberItems.Add(pb.Runs);
                }

                continue;
            }

            // Check for heading via QuickStyleDef
            var quickStyleIndex = oe.Attribute("quickStyleIndex")?.Value;
            var isHeading = false;
            if (quickStyleIndex is not null && headingStyles.TryGetValue(quickStyleIndex, out var headingInfo))
            {
                if (textContent is ParagraphBlock hpb)
                {
                    var headingText = string.Join("", hpb.Runs.Select(r => r.Text));
                    if (!string.IsNullOrWhiteSpace(headingText))
                    {
                        // Apply QuickStyleDef style to runs (merge with existing inline styles)
                        var styledRuns = hpb.Runs.Select(r => r with
                        {
                            Style = new TextStyleStyle(
                                Bold: r.Style.Bold || headingInfo.style.Bold,
                                Italic: r.Style.Italic || headingInfo.style.Italic,
                                Underline: r.Style.Underline || headingInfo.style.Underline,
                                Strikethrough: r.Style.Strikethrough || headingInfo.style.Strikethrough,
                                Code: r.Style.Code,
                                ForegroundColor: r.Style.ForegroundColor ?? headingInfo.style.ForegroundColor,
                                BackgroundColor: r.Style.BackgroundColor ?? headingInfo.style.BackgroundColor
                            )
                        }).ToList();

                        blocks.Add(new HeadingBlock(headingInfo.level, headingText, styledRuns));
                        isHeading = true;
                    }
                }
            }

            if (!isHeading && textContent is not null)
            {
                blocks.Add(textContent);
            }

            // Process nested OEChildren (sub-items) — always, even for headings
            var nestedChildren = oe.Element(OneNs + "OEChildren");
            if (nestedChildren is not null)
            {
                ProcessOEChildren(nestedChildren, blocks, headingStyles, depth + 1);
            }
        }

        // Flush any remaining list items
        if (pendingBulletItems.Count > 0)
        {
            blocks.Add(new BulletedListBlock(pendingBulletItems.ToList()));
        }

        if (pendingNumberItems.Count > 0)
        {
            blocks.Add(new NumberedListBlock(pendingNumberItems.ToList()));
        }
    }

    private static ParagraphBlock? CollectOEText(XElement oe, int depth)
    {
        var allRuns = new List<TextRun>();
        var alignment = ParagraphAlignment.Left;
        var layout = LayoutHint.Normal;
        var hasTextElement = false;

        // Get base style from OE element (same logic as table cells)
        var oeStyleAttr = oe.Attribute("style")?.Value ?? string.Empty;
        var oeBaseStyle = ParseElementStyle(oeStyleAttr);

        foreach (var t in oe.Elements(OneNs + "T"))
        {
            hasTextElement = true;
            var paragraph = ParseParagraph(t);
            if (paragraph is not null)
            {
                // Merge OE style with inline styles from CDATA
                var styledRuns = paragraph.Runs.Select(r => r with
                {
                    Style = new TextStyleStyle(
                        Bold: r.Style.Bold || oeBaseStyle.Bold,
                        Italic: r.Style.Italic || oeBaseStyle.Italic,
                        Underline: r.Style.Underline || oeBaseStyle.Underline,
                        Strikethrough: r.Style.Strikethrough || oeBaseStyle.Strikethrough,
                        Code: r.Style.Code,
                        ForegroundColor: r.Style.ForegroundColor ?? oeBaseStyle.ForegroundColor,
                        BackgroundColor: r.Style.BackgroundColor ?? oeBaseStyle.BackgroundColor
                    )
                });
                allRuns.AddRange(styledRuns);

                // Keep first non-default alignment / layout encountered
                if (alignment == ParagraphAlignment.Left && paragraph.Alignment != ParagraphAlignment.Left)
                {
                    alignment = paragraph.Alignment;
                }

                if (layout == LayoutHint.Normal && paragraph.Layout != LayoutHint.Normal)
                {
                    layout = paragraph.Layout;
                }
            }
        }

        if (allRuns.Count == 0)
        {
            // Preserve intentionally blank OneNote lines (empty <one:T>) as empty paragraphs.
            return hasTextElement ? new ParagraphBlock(Array.Empty<TextRun>(), alignment, layout, depth) : null;
        }

        return new ParagraphBlock(allRuns, alignment, layout, depth);
    }

    private TableBlock? ParseTable(XElement tableElement)
    {
        var rows = tableElement
            .Elements(OneNs + "Row")
            .Select(row =>
                (IReadOnlyList<IReadOnlyList<TextRun>>)row
                    .Elements(OneNs + "Cell")
                    .Select(ParseCellRuns)
                    .ToList())
            .Where(row => row.Count > 0)
            .ToList();

        return rows.Count > 0 ? new TableBlock(rows) : null;
    }

    private List<SemanticBlock> ParseImagesFromTable(XElement tableElement)
    {
        var imageBlocks = new List<SemanticBlock>();

        foreach (var imageElement in tableElement.Descendants(OneNs + "Image"))
        {
            var imageBlock = ParseImage(imageElement, out var imageError);
            if (imageBlock is not null)
            {
                imageBlocks.Add(imageBlock);
            }
            else
            {
                var degradeMessage = string.IsNullOrWhiteSpace(imageError)
                    ? "图片解析失败"
                    : $"图片处理失败: {imageError}";
                imageBlocks.Add(new UnsupportedBlock("Image", imageElement.ToString(), degradeMessage));
            }
        }

        return imageBlocks;
    }

    private IReadOnlyList<TextRun> ParseCellRuns(XElement cell)
    {
        var cellShadingColor = cell.Attribute("shadingColor")?.Value;

        var runs = new List<TextRun>();
        
        // Process OE elements in the cell (they contain style attributes)
        var oeChildren = cell.Element(OneNs + "OEChildren");
        if (oeChildren is not null)
        {
            foreach (var oe in oeChildren.Elements(OneNs + "OE"))
            {
                // Get base style from OE element
                var oeStyleAttr = oe.Attribute("style")?.Value ?? string.Empty;
                var oeBaseStyle = ParseElementStyle(oeStyleAttr);

                foreach (var t in oe.Elements(OneNs + "T"))
                {
                    var paragraph = ParseParagraph(t);
                    if (paragraph is not null)
                    {
                        // Merge OE style with inline styles from CDATA
                        var styledRuns = paragraph.Runs.Select(r => r with
                        {
                            Style = new TextStyleStyle(
                                Bold: r.Style.Bold || oeBaseStyle.Bold,
                                Italic: r.Style.Italic || oeBaseStyle.Italic,
                                Underline: r.Style.Underline || oeBaseStyle.Underline,
                                Strikethrough: r.Style.Strikethrough || oeBaseStyle.Strikethrough,
                                Code: r.Style.Code,
                                ForegroundColor: r.Style.ForegroundColor ?? oeBaseStyle.ForegroundColor,
                                BackgroundColor: r.Style.BackgroundColor ?? oeBaseStyle.BackgroundColor
                            )
                        });
                        runs.AddRange(styledRuns);
                    }
                }
            }
        }

        // Apply cell background color to runs that don't already have one
        if (!string.IsNullOrWhiteSpace(cellShadingColor))
        {
            runs = runs.Select(r =>
                string.IsNullOrWhiteSpace(r.Style.BackgroundColor)
                    ? r with { Style = r.Style with { BackgroundColor = cellShadingColor } }
                    : r
            ).ToList();
        }

        if (runs.Count == 0)
        {
            var style = !string.IsNullOrWhiteSpace(cellShadingColor)
                ? new TextStyleStyle(BackgroundColor: cellShadingColor)
                : new TextStyleStyle();
            runs.Add(new TextRun(string.Empty, style));
        }

        return runs;
    }

    private static ParagraphBlock? ParseParagraph(XElement textElement)
    {
        // Read style from the <one:T> element itself — OneNote stores color/bold/italic here
        var elementStyleAttr = textElement.Attribute("style")?.Value ?? string.Empty;
        var baseStyle = ParseElementStyle(elementStyleAttr);

        var cdata = textElement.Nodes().OfType<XCData>().FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(cdata))
        {
            var plainText = NormalizeLineEndings(textElement.Value);
            if (string.IsNullOrWhiteSpace(plainText))
            {
                return null;
            }

            return new ParagraphBlock([new TextRun(plainText, baseStyle)]);
        }

        var html = new HtmlAgilityPack.HtmlDocument();
        html.LoadHtml(cdata);

        var alignment = ParseAlignment(cdata);
        var layoutHint = ParseLayoutHint(cdata);
        var runs = new List<TextRun>();

        foreach (var node in html.DocumentNode.ChildNodes)
        {
            CollectRuns(node, baseStyle, null, runs);
        }

        runs = runs.Where(r => !string.IsNullOrEmpty(r.Text)).ToList();
        if (runs.Count == 0)
        {
            return null;
        }

        return new ParagraphBlock(runs, alignment, layoutHint);
    }

    /// <summary>
    /// Parse CSS-like style attribute from a OneNote XML element (e.g. &lt;one:T style="..."&gt;)
    /// into a base TextStyleStyle.
    /// </summary>
    private static TextStyleStyle ParseElementStyle(string inlineStyle)
    {
        if (string.IsNullOrWhiteSpace(inlineStyle)) return new TextStyleStyle();

        var bold = inlineStyle.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase)
                   || inlineStyle.Contains("font-weight: bold", StringComparison.OrdinalIgnoreCase)
                   || Regex.IsMatch(inlineStyle, @"font-weight\s*:\s*[5-9]\d{2}", RegexOptions.IgnoreCase);

        var italic = inlineStyle.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase)
                     || inlineStyle.Contains("font-style: italic", StringComparison.OrdinalIgnoreCase);

        var underline = Regex.IsMatch(inlineStyle, @"text-decoration\s*:\s*[^;]*underline", RegexOptions.IgnoreCase);
        var strikethrough = Regex.IsMatch(inlineStyle, @"text-decoration\s*:\s*[^;]*line-through", RegexOptions.IgnoreCase);

        var foreground = ExtractCssColor(inlineStyle, "color");
        var background = ExtractCssColor(inlineStyle, "background-color")
                         ?? ExtractCssBackgroundShorthand(inlineStyle);

        return new TextStyleStyle(bold, italic, underline, strikethrough, false, foreground, background);
    }

    private static void CollectRuns(HtmlAgilityPack.HtmlNode node, TextStyleStyle inheritedStyle, string? inheritedLink, ICollection<TextRun> runs)
    {
        if (node.NodeType == HtmlAgilityPack.HtmlNodeType.Text)
        {
            var text = NormalizeLineEndings(HtmlAgilityPack.HtmlEntity.DeEntitize(node.InnerText));
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                var containsLineBreak = text.Contains('\n');
                var atLogicalLineStart = runs.Count == 0 || runs.Last().Text.EndsWith('\n');

                // Preserve whitespace-only text only when it carries visible structure:
                // indentation at line start or explicit line-break content.
                if (!containsLineBreak && !atLogicalLineStart)
                {
                    return;
                }
            }

            runs.Add(new TextRun(text, inheritedStyle, inheritedLink));
            return;
        }

        var style = inheritedStyle;
        var link = inheritedLink;

        switch (node.Name.ToLowerInvariant())
        {
            case "strong":
            case "b":
                style = style with { Bold = true };
                break;
            case "em":
            case "i":
                style = style with { Italic = true };
                break;
            case "u":
                style = style with { Underline = true };
                break;
            case "s":
            case "strike":
                style = style with { Strikethrough = true };
                break;
            case "code":
                style = style with { Code = true };
                break;
            case "a":
                link = node.GetAttributeValue("href", inheritedLink);
                break;
            case "br":
                runs.Add(new TextRun("\n", inheritedStyle, inheritedLink));
                return;
            case "del":
                style = style with { Strikethrough = true };
                break;
            case "ins":
                style = style with { Underline = true };
                break;
            case "mark":
                style = style with { BackgroundColor = style.BackgroundColor ?? "#FFFF00" };
                break;
            case "font":
            {
                var fontColor = node.GetAttributeValue("color", string.Empty);
                if (!string.IsNullOrWhiteSpace(fontColor))
                {
                    style = style with { ForegroundColor = fontColor };
                }
                break;
            }
        }

        var inlineStyle = node.GetAttributeValue("style", string.Empty);
        if (inlineStyle.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase)
            || inlineStyle.Contains("font-weight: bold", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(inlineStyle, @"font-weight\s*:\s*[5-9]\d{2}", RegexOptions.IgnoreCase))
        {
            style = style with { Bold = true };
        }

        if (inlineStyle.Contains("font-style:italic", StringComparison.OrdinalIgnoreCase)
            || inlineStyle.Contains("font-style: italic", StringComparison.OrdinalIgnoreCase))
        {
            style = style with { Italic = true };
        }

        if (Regex.IsMatch(inlineStyle, "text-decoration\\s*:\\s*[^;]*underline", RegexOptions.IgnoreCase))
        {
            style = style with { Underline = true };
        }

        if (Regex.IsMatch(inlineStyle, "text-decoration\\s*:\\s*[^;]*line-through", RegexOptions.IgnoreCase))
        {
            style = style with { Strikethrough = true };
        }

        var color = ExtractCssColor(inlineStyle, "color");
        if (!string.IsNullOrWhiteSpace(color))
        {
            style = style with { ForegroundColor = color };
        }

        var background = ExtractCssColor(inlineStyle, "background-color")
                         ?? ExtractCssBackgroundShorthand(inlineStyle);
        if (!string.IsNullOrWhiteSpace(background))
        {
            style = style with { BackgroundColor = background };
        }

        foreach (var child in node.ChildNodes)
        {
            CollectRuns(child, style, link, runs);
        }
    }

    private static ParagraphAlignment ParseAlignment(string html)
    {
        if (Regex.IsMatch(html, "text-align\\s*:\\s*center", RegexOptions.IgnoreCase))
        {
            return ParagraphAlignment.Center;
        }

        if (Regex.IsMatch(html, "text-align\\s*:\\s*right", RegexOptions.IgnoreCase))
        {
            return ParagraphAlignment.Right;
        }

        if (Regex.IsMatch(html, "text-align\\s*:\\s*justify", RegexOptions.IgnoreCase))
        {
            return ParagraphAlignment.Justify;
        }

        return ParagraphAlignment.Left;
    }

    private static LayoutHint ParseLayoutHint(string html)
    {
        if (Regex.IsMatch(html, "position\\s*:\\s*absolute", RegexOptions.IgnoreCase))
        {
            return LayoutHint.AbsolutePositioned;
        }

        if (Regex.IsMatch(html, "float\\s*:", RegexOptions.IgnoreCase))
        {
            return LayoutHint.FloatingObject;
        }

        if (Regex.IsMatch(html, "column-count\\s*:", RegexOptions.IgnoreCase))
        {
            return LayoutHint.ColumnLike;
        }

        return LayoutHint.Normal;
    }

    private static string? ExtractCssColor(string inlineStyle, string propertyName)
    {
        var match = Regex.Match(inlineStyle, $"(?<![\\w-]){Regex.Escape(propertyName)}\\s*:\\s*([^;]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value.Trim();
    }

    /// <summary>
    /// Extract color from CSS background shorthand (e.g. "background: yellow", "background: #FF0").
    /// Only matches when explicit background-color is absent.
    /// </summary>
    private static string? ExtractCssBackgroundShorthand(string inlineStyle)
    {
        var match = Regex.Match(inlineStyle, @"(?<!\w-)background\s*:\s*([^;]+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var value = match.Groups[1].Value.Trim();
        // Only pick up color-like values (hex, rgb, named color), skip url(...) / gradients
        if (value.StartsWith("url", StringComparison.OrdinalIgnoreCase)
            || value.Contains("gradient", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Take the first token that looks like a color
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('#') || token.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)
                || IsNamedCssColor(token))
            {
                return token;
            }
        }

        return null;
    }

    private static bool IsNamedCssColor(string token)
    {
        return token.ToLowerInvariant() switch
        {
            "red" or "green" or "blue" or "yellow" or "orange" or "purple" or "pink"
            or "gray" or "grey" or "black" or "white" or "brown" or "cyan" or "magenta"
            or "lime" or "maroon" or "navy" or "olive" or "teal" or "aqua" or "fuchsia"
            or "silver" or "transparent" => true,
            _ => false
        };
    }

    private static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static AttachmentBlock? ParseInsertedFile(XElement? insertedFileElement)
    {
        if (insertedFileElement is null) return null;

        try
        {
            var preferredName = insertedFileElement.Attribute("preferredName")?.Value;
            var pathCache = insertedFileElement.Attribute("pathCache")?.Value;
            var fileName = !string.IsNullOrWhiteSpace(preferredName)
                ? preferredName
                : !string.IsNullOrWhiteSpace(pathCache)
                    ? Path.GetFileName(pathCache)
                    : "附件";

            // 先提取基本信息（size、mimeType 等）
            var dataElement = insertedFileElement.Element(OneNs + "Data");
            var fileDataBase64 = dataElement?.Value.Trim();

            var mimeType = insertedFileElement.Attribute("mimeType")?.Value;
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                mimeType = InferMimeType(fileName);
            }

            var fileSize = 0L;
            var sizeText = insertedFileElement.Attribute("size")?.Value
                           ?? insertedFileElement.Attribute("fileSize")?.Value;
            if (!string.IsNullOrWhiteSpace(sizeText) && long.TryParse(sizeText, out var parsedSize))
            {
                fileSize = parsedSize;
            }
            else if (!string.IsNullOrWhiteSpace(fileDataBase64))
            {
                // base64 长度转原始字节数的近似值，用于诊断展示
                fileSize = (long)Math.Floor(fileDataBase64.Length * 0.75);
            }

            // 检查文件大小限制（100MB）
            const long maxFileSize = 100 * 1024 * 1024; // 100MB
            if (fileSize > maxFileSize)
            {
                var sizeMB = fileSize / (1024.0 * 1024.0);
                var errorMsg = $"文件超过大小限制: {sizeMB:F1}MB > 100MB";
                DiagnosticLogger.Warn($"附件{errorMsg}: {fileName}");
                return new AttachmentBlock(fileName, pathCache, null, mimeType, fileSize, null, errorMsg);
            }

            // 检查文件是否存在（如果提供了本地路径）
            if (!string.IsNullOrWhiteSpace(pathCache))
            {
                try
                {
                    if (!File.Exists(pathCache))
                    {
                        DiagnosticLogger.Warn($"附件文件不存在: {fileName} (路径: {pathCache})");
                        return new AttachmentBlock(fileName, pathCache, null, mimeType, fileSize, null, "文件不存在");
                    }
                }
                catch (Exception ex)
                {
                    var errorMessage = $"检查文件失败: {ex.Message}";
                    DiagnosticLogger.Warn($"检查附件文件存在性失败: {fileName}, 错误: {ex.Message}");
                    return new AttachmentBlock(fileName, pathCache, null, mimeType, fileSize, null, errorMessage);
                }
            }

            // 读取小文件内容（< 10MB）到 base64
            string? base64Data = null;
            const long maxInMemorySize = 10 * 1024 * 1024; // 10MB
            if (!string.IsNullOrWhiteSpace(fileDataBase64))
            {
                base64Data = fileDataBase64;
            }
            else if (!string.IsNullOrWhiteSpace(pathCache) && File.Exists(pathCache) && fileSize <= maxInMemorySize)
            {
                try
                {
                    var fileBytes = File.ReadAllBytes(pathCache);
                    base64Data = Convert.ToBase64String(fileBytes);
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Warn($"读取附件文件失败: {fileName}, 错误: {ex.Message}");
                    return new AttachmentBlock(fileName, pathCache, null, mimeType, fileSize, null, $"读取失败: {ex.Message}");
                }
            }

            return new AttachmentBlock(fileName, pathCache, base64Data, mimeType, fileSize);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn($"附件解析失败: {ex.Message}");
            return null;
        }
    }

    private static string InferMimeType(string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".txt" => "text/plain",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// 解析 OneNote 图片元素
    /// </summary>
    private ImageBlock? ParseImage(XElement? imageElement, out string? error)
    {
        error = null;
        if (imageElement is null)
        {
            error = "图片元素为空";
            return null;
        }

        try
        {
            var dataElement = imageElement.Element(OneNs + "Data");
            if (dataElement is null)
            {
                error = "缺少 Data 节点";
                return null;
            }

            var base64Data = dataElement.Value.Trim();
            if (string.IsNullOrWhiteSpace(base64Data))
            {
                error = "Data 节点为空";
                return null;
            }

            var format = ResolveImageFormat(imageElement, dataElement);
            var caption = ExtractImageCaption(imageElement);
            var (width, height) = ExtractImageSize(imageElement);

            // 在解析层执行图片处理，确保后续映射层拿到的是可直接使用的数据
            var resizer = new ImageResizer();
            var result = resizer.ProcessImage(base64Data, format);

            if (!result.Success)
            {
                error = result.ErrorMessage ?? "图片处理失败";
                DiagnosticLogger.Warn(
                    $"图片处理失败: format={format}, originalSize={result.OriginalSize}, reason={error}");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                DiagnosticLogger.Warn(
                    $"图片处理告警: format={result.OriginalFormat}, originalSize={result.OriginalSize}, finalSize={result.FinalSize}, detail={result.ErrorMessage}");
            }

            return new ImageBlock(
                result.DataUri,
                caption,
                result.OriginalFormat,
                result.OriginalSize,
                result.FinalSize,
                IsProcessed: true,
                ProcessingError: result.ErrorMessage,
                Width: width,
                Height: height);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            DiagnosticLogger.Warn($"图片解析失败: {ex.Message}");
            return null;
        }
    }

    private static string ResolveImageFormat(XElement imageElement, XElement dataElement)
    {
        var imageLevel = imageElement.Attribute("format")?.Value;
        if (!string.IsNullOrWhiteSpace(imageLevel))
        {
            return imageLevel.ToLowerInvariant();
        }

        var dataLevel = dataElement.Attribute("format")?.Value;
        if (!string.IsNullOrWhiteSpace(dataLevel))
        {
            return dataLevel.ToLowerInvariant();
        }

        return "png";
    }

    /// <summary>
    /// 从图片元素中提取 Size（宽高），解析失败时回退 0。
    /// </summary>
    private static (int width, int height) ExtractImageSize(XElement imageElement)
    {
        var sizeElement = imageElement.Element(OneNs + "Size");
        if (sizeElement is null)
        {
            return (0, 0);
        }

        int width = 0;
        int height = 0;

        foreach (var attr in sizeElement.Attributes())
        {
            var name = attr.Name.LocalName;
            var value = attr.Value;

            if (width == 0 && (name.Equals("width", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("w", StringComparison.OrdinalIgnoreCase)
                               || name.Equals("cx", StringComparison.OrdinalIgnoreCase)))
            {
                width = ParseDimensionValue(value);
            }

            if (height == 0 && (name.Equals("height", StringComparison.OrdinalIgnoreCase)
                                || name.Equals("h", StringComparison.OrdinalIgnoreCase)
                                || name.Equals("cy", StringComparison.OrdinalIgnoreCase)))
            {
                height = ParseDimensionValue(value);
            }

            if (width > 0 && height > 0)
            {
                break;
            }
        }

        return (width, height);
    }

    private static int ParseDimensionValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        if (int.TryParse(raw, out var parsed) && parsed > 0)
        {
            return parsed;
        }

        var match = Regex.Match(raw, @"\d+");
        if (match.Success && int.TryParse(match.Value, out parsed) && parsed > 0)
        {
            return parsed;
        }

        return 0;
    }

    /// <summary>
    /// 从图片元素中提取 Caption
    /// </summary>
    private static string ExtractImageCaption(XElement imageElement)
    {
        // 尝试从 Size 元素的 caption 属性提取
        var sizeElement = imageElement.Element(OneNs + "Size");
        if (sizeElement is not null)
        {
            var caption = sizeElement.Attribute("caption")?.Value;
            if (!string.IsNullOrWhiteSpace(caption))
            {
                return caption;
            }
        }

        // 尝试从 OCRText 元素提取
        var ocrTextElement = imageElement.Element(OneNs + "OCRText");
        if (ocrTextElement is not null)
        {
            var ocrText = ocrTextElement.Value.Trim();
            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                return ocrText;
            }
        }

        // 尝试从 Data 元素的 lang 属性提取
        var dataElement = imageElement.Element(OneNs + "Data");
        if (dataElement is not null)
        {
            var lang = dataElement.Attribute("lang")?.Value;
            if (!string.IsNullOrWhiteSpace(lang))
            {
                return lang;
            }
        }

        return string.Empty;
    }

}
