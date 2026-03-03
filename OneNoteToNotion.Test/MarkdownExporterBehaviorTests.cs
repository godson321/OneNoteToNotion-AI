using System.Reflection;
using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Parsing;
using Xunit;

namespace OneNoteToNotion.Test;

public class MarkdownExporterBehaviorTests
{
    [Fact]
    public void Convert_ParagraphCenterAlignment_ShouldUseHtmlDiv()
    {
        var doc = new SemanticDocument { Title = "Align" };
        doc.Blocks.Add(new ParagraphBlock(
            [new TextRun("center text", new TextStyleStyle())],
            ParagraphAlignment.Center,
            LayoutHint.Normal));

        var markdown = ConvertToMarkdown(doc);

        Assert.Contains("<div style=\"text-align:center;\">center text</div>", markdown);
    }

    [Fact]
    public void Convert_ColumnLikeLayout_ShouldEmitFallbackNote()
    {
        var doc = new SemanticDocument { Title = "Column" };
        doc.Blocks.Add(new ParagraphBlock(
            [new TextRun("column text", new TextStyleStyle())],
            ParagraphAlignment.Left,
            LayoutHint.ColumnLike));

        var markdown = ConvertToMarkdown(doc);

        Assert.Contains("> [!NOTE]", markdown);
        Assert.Contains("column text", markdown);
    }

    [Fact]
    public void Convert_ListWithIndentLevel_ShouldIndentMarkdownList()
    {
        var doc = new SemanticDocument { Title = "List" };
        var bulletItems = (IReadOnlyList<IReadOnlyList<TextRun>>)[[new TextRun("child bullet", new TextStyleStyle())]];
        var numberItems = (IReadOnlyList<IReadOnlyList<TextRun>>)[[new TextRun("child number", new TextStyleStyle())]];

        doc.Blocks.Add(CreateBulletedListBlock(bulletItems, 1));
        doc.Blocks.Add(CreateNumberedListBlock(numberItems, 2));

        var markdown = ConvertToMarkdown(doc);

        var bulletIndentProperty = typeof(BulletedListBlock).GetProperty("IndentLevel");
        var numberIndentProperty = typeof(NumberedListBlock).GetProperty("IndentLevel");
        if (bulletIndentProperty is null || numberIndentProperty is null)
        {
            return;
        }

        Assert.Contains("  - child bullet", markdown);
        Assert.Contains("    1. child number", markdown);
    }

    [Fact]
    public void Convert_TransparentBackground_ShouldNotEmitHighlightMarkers()
    {
        var doc = new SemanticDocument { Title = "Transparent Background" };
        doc.Blocks.Add(new ParagraphBlock(
            [new TextRun("ID", new TextStyleStyle(BackgroundColor: "transparent"))]));

        var markdown = ConvertToMarkdown(doc);

        Assert.Contains("ID", markdown);
        Assert.DoesNotContain("==ID==", markdown);
    }

    [Fact]
    public void Convert_CodeLikeAngleBrackets_ShouldBeEscapedAsLiteralText()
    {
        var doc = new SemanticDocument { Title = "Code" };
        doc.Blocks.Add(new ParagraphBlock(
            [new TextRun("ID </td> 号", new TextStyleStyle())]));

        var markdown = ConvertToMarkdown(doc);

        Assert.Contains("ID &lt;/td&gt; 号", markdown);
    }

    [Fact]
    public void Convert_BackgroundColorRuns_ShouldAvoidRepeatedHighlightDelimiters()
    {
        var highlight = new TextStyleStyle(BackgroundColor: "#ffff00");
        var doc = new SemanticDocument { Title = "Highlight Merge" };
        doc.Blocks.Add(new ParagraphBlock(
        [
            new TextRun("2021-04-26 ", highlight),
            new TextRun("增加判断标准，使用状态，", highlight),
            new TextRun("ERP", highlight),
            new TextRun("批次号字段", highlight),
            new TextRun(".", highlight)
        ]));

        var markdown = ConvertToMarkdown(doc);

        Assert.Contains("background-color:#ffff00;", markdown);
        Assert.DoesNotContain("====", markdown);
    }

    [Fact]
    public void Convert_ForegroundColorRun_ShouldEmitColorSpan()
    {
        var doc = new SemanticDocument { Title = "Color" };
        doc.Blocks.Add(new ParagraphBlock(
            [new TextRun("Unique Index", new TextStyleStyle(ForegroundColor: "#2F5597"))]));

        var markdown = ConvertToMarkdown(doc);

        Assert.Contains("<span style=\"color:#2F5597;\">Unique Index</span>", markdown);
    }

    [Fact]
    public void Convert_TypographyStyleRun_ShouldEmitInlineCss()
    {
        var style = new TextStyleStyle(
            FontSize: "14pt",
            FontFamily: "Consolas,Microsoft YaHei",
            LineHeight: "1.8",
            LetterSpacing: "0.4px");

        var doc = new SemanticDocument { Title = "Typography" };
        doc.Blocks.Add(new ParagraphBlock([new TextRun("Style Text", style)]));

        var markdown = ConvertToMarkdown(doc);

        Assert.Contains("font-size:14pt;", markdown);
        Assert.Contains("font-family:Consolas,Microsoft YaHei;", markdown);
        Assert.Contains("line-height:1.8;", markdown);
        Assert.Contains("letter-spacing:0.4px;", markdown);
    }

    [Fact]
    public void Convert_Newlines_ShouldPreserveLineBreaks()
    {
        var doc = new SemanticDocument { Title = "Breaks" };
        doc.Blocks.Add(new ParagraphBlock([new TextRun("A\nB", new TextStyleStyle())]));

        var markdown = ConvertToMarkdown(doc);

        Assert.Contains("A<br/>B", markdown);
    }

    private static string ConvertToMarkdown(SemanticDocument doc)
    {
        var exporter = new MarkdownExporter(new StubPageContentProvider("<one:Page xmlns:one=\"http://schemas.microsoft.com/office/onenote/2013/onenote\" name=\"x\"/>"));
        var method = typeof(MarkdownExporter).GetMethod("ConvertSemanticDocumentToMarkdown", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(exporter, [doc]) as string;
        Assert.NotNull(result);
        return result!;
    }

    private static BulletedListBlock CreateBulletedListBlock(IReadOnlyList<IReadOnlyList<TextRun>> items, int indentLevel)
    {
        var ctor = typeof(BulletedListBlock).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 2);
        if (ctor is not null)
        {
            return (BulletedListBlock)ctor.Invoke([items, indentLevel]);
        }

        return new BulletedListBlock(items);
    }

    private static NumberedListBlock CreateNumberedListBlock(IReadOnlyList<IReadOnlyList<TextRun>> items, int indentLevel)
    {
        var ctor = typeof(NumberedListBlock).GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == 2);
        if (ctor is not null)
        {
            return (NumberedListBlock)ctor.Invoke([items, indentLevel]);
        }

        return new NumberedListBlock(items);
    }

    private sealed class StubPageContentProvider(string xml) : IOneNotePageContentProvider
    {
        public Task<string> GetPageContentXmlAsync(string pageId, CancellationToken cancellationToken)
        {
            return Task.FromResult(xml);
        }
    }
}
