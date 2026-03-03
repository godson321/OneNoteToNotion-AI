using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using Xunit;

namespace OneNoteToNotion.Test;

public class OneNoteXmlSemanticParserCodeLikeTextTests
{
    [Fact]
    public void Parse_CodeLikeTableTagFragment_ShouldPreserveLiteralCloserText()
    {
        const string xml = """
<?xml version="1.0"?>
<one:Page xmlns:one="http://schemas.microsoft.com/office/onenote/2013/onenote" name="CodeLike">
  <one:Outline>
    <one:OEChildren>
      <one:OE>
        <one:T><![CDATA[<span style="font-family:Consolas">ID </td> 号</span>]]></one:T>
      </one:OE>
    </one:OEChildren>
  </one:Outline>
</one:Page>
""";

        var parser = new OneNoteXmlSemanticParser();
        var document = parser.Parse(xml);

        var paragraph = Assert.IsType<ParagraphBlock>(Assert.Single(document.Blocks));
        var text = string.Concat(paragraph.Runs.Select(r => r.Text));
        Assert.Contains("</td>", text);
    }
}

