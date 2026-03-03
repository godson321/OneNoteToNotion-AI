using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using Xunit;

namespace OneNoteToNotion.Test;

public class OneNoteXmlSemanticParserStylePreservationTests
{
    [Fact]
    public void Parse_Heading6QuickStyle_ShouldPreserveLevel6()
    {
        const string xml = """
<?xml version="1.0"?>
<one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" name="Heading Test">
  <one:QuickStyleDef index="7" name="Heading 6" />
  <one:Outline>
    <one:OEChildren>
      <one:OE quickStyleIndex="7">
        <one:T>Deep Heading</one:T>
      </one:OE>
    </one:OEChildren>
  </one:Outline>
</one:Page>
""";

        var parser = new OneNoteXmlSemanticParser();
        var document = parser.Parse(xml);

        var heading = Assert.IsType<HeadingBlock>(Assert.Single(document.Blocks));
        Assert.Equal(6, heading.Level);
        Assert.Equal("Deep Heading", heading.Text);
    }

    [Fact]
    public void Parse_TextElementStyle_ShouldCaptureTypographyFields()
    {
        const string xml = """
<?xml version="1.0"?>
<one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" name="Style Test">
  <one:Outline>
    <one:OEChildren>
      <one:OE>
        <one:T style="font-size:14pt;font-family:'Consolas', 'Microsoft YaHei';line-height:1.8;letter-spacing:0.3px;color:#2F5597;background-color:#FFF2CC;">Style Text</one:T>
      </one:OE>
    </one:OEChildren>
  </one:Outline>
</one:Page>
""";

        var parser = new OneNoteXmlSemanticParser();
        var document = parser.Parse(xml);

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(document.Blocks));
        var run = Assert.Single(paragraph.Runs);
        Assert.Equal("Style Text", run.Text);
        Assert.Equal("14pt", run.Style.FontSize);
        Assert.Equal("Consolas, Microsoft YaHei", run.Style.FontFamily);
        Assert.Equal("1.8", run.Style.LineHeight);
        Assert.Equal("0.3px", run.Style.LetterSpacing);
        Assert.Equal("#2F5597", run.Style.ForegroundColor);
        Assert.Equal("#FFF2CC", run.Style.BackgroundColor);
    }
}

