using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Parsing;
using Xunit;

namespace OneNoteToNotion.Test;

public class MarkdownExporterTableImageTests
{
    [Fact]
    public async Task Export_TableCellDataUriImage_ShouldWriteLocalImageAndRenderHtmlImageTag()
    {
        var provider = new StubPageContentProvider(BuildTableWithImageXml());
        var exporter = new MarkdownExporter(provider);
        var node = new OneNoteTreeNode
        {
            Id = "page-1",
            Name = "table-image",
            NodeType = OneNoteNodeType.Page
        };

        var outputRoot = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "TempExport",
            Guid.NewGuid().ToString("N"));

        try
        {
            var result = await exporter.ExportAsync([node], outputRoot, cancellationToken: CancellationToken.None);
            Assert.Equal(1, result.ExportedFiles);

            var markdownPath = Path.Combine(outputRoot, "table-image.md");
            Assert.True(File.Exists(markdownPath), $"Expected markdown file to exist: {markdownPath}");

            var markdown = File.ReadAllText(markdownPath).Replace('\\', '/');
            Assert.Contains("<table", markdown);
            Assert.Contains("<img ", markdown);
            Assert.DoesNotContain("src=\"data:image/", markdown);

            var imageDirectory = Path.Combine(outputRoot, "table-image_images");
            Assert.True(Directory.Exists(imageDirectory), $"Expected image directory to exist: {imageDirectory}");

            var files = Directory.GetFiles(imageDirectory);
            Assert.Single(files);
            var imageFileName = Path.GetFileName(files[0]);
            Assert.Contains($"src=\"table-image_images/{imageFileName}\"", markdown);

            Assert.DoesNotContain("!\\[", markdown);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                try
                {
                    Directory.Delete(outputRoot, recursive: true);
                }
                catch (IOException)
                {
                    // Ignore cleanup errors on CI/test runners that still hold transient file handles.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore cleanup errors on CI/test runners that still hold transient file handles.
                }
            }
        }
    }

    [Fact]
    public async Task Export_TableWithMergedAndStyledCell_ShouldKeepSpanAndStyles()
    {
        var provider = new StubPageContentProvider(BuildMergedStyledTableXml());
        var exporter = new MarkdownExporter(provider);
        var node = new OneNoteTreeNode
        {
            Id = "page-2",
            Name = "table-merge-style",
            NodeType = OneNoteNodeType.Page
        };

        var outputRoot = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "TempExport",
            Guid.NewGuid().ToString("N"));

        try
        {
            var result = await exporter.ExportAsync([node], outputRoot, cancellationToken: CancellationToken.None);
            Assert.Equal(1, result.ExportedFiles);

            var markdownPath = Path.Combine(outputRoot, "table-merge-style.md");
            Assert.True(File.Exists(markdownPath), $"Expected markdown file to exist: {markdownPath}");

            var markdown = File.ReadAllText(markdownPath).Replace('\\', '/');
            Assert.Contains("rowspan=\"2\"", markdown);
            Assert.Contains("colspan=\"2\"", markdown);
            Assert.Contains("background-color:#ffff00", markdown.ToLowerInvariant());
            Assert.Contains("<strong>", markdown);
            Assert.Contains("color:#ff0000", markdown.ToLowerInvariant());
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                try
                {
                    Directory.Delete(outputRoot, recursive: true);
                }
                catch (IOException)
                {
                    // Ignore cleanup errors on CI/test runners that still hold transient file handles.
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore cleanup errors on CI/test runners that still hold transient file handles.
                }
            }
        }
    }

    private static string BuildTableWithImageXml()
    {
        const string onePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO5x8f8AAAAASUVORK5CYII=";

        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<one:Page xmlns:one=""http://schemas.microsoft.com/office/onenote/2013/onenote"" name=""Table Image Page"">
  <one:Outline>
    <one:OEChildren>
      <one:OE>
        <one:Table>
          <one:Row>
            <one:Cell>
              <one:OEChildren>
                <one:OE>
                  <one:T><![CDATA[<span>Header</span>]]></one:T>
                </one:OE>
              </one:OEChildren>
            </one:Cell>
          </one:Row>
          <one:Row>
            <one:Cell>
              <one:OEChildren>
                <one:OE>
                  <one:Image format=""png"">
                    <one:Data>{onePixelPngBase64}</one:Data>
                    <one:Size width=""1"" height=""1"" caption=""sample|caption"" />
                  </one:Image>
                </one:OE>
              </one:OEChildren>
            </one:Cell>
          </one:Row>
        </one:Table>
      </one:OE>
    </one:OEChildren>
  </one:Outline>
</one:Page>";
    }

    private static string BuildMergedStyledTableXml()
    {
        return @"<?xml version=""1.0"" encoding=""utf-8""?>
<one:Page xmlns:one=""http://schemas.microsoft.com/office/onenote/2013/onenote"" name=""Merged Style Page"">
  <one:Outline>
    <one:OEChildren>
      <one:OE>
        <one:Table>
          <one:Row>
            <one:Cell rowSpan=""2"" colSpan=""2"" shadingColor=""#ffff00"">
              <one:OEChildren>
                <one:OE>
                  <one:T style=""color:#ff0000;font-weight:bold;""><![CDATA[<span>Merged</span>]]></one:T>
                </one:OE>
              </one:OEChildren>
            </one:Cell>
            <one:Cell>
              <one:OEChildren>
                <one:OE>
                  <one:T><![CDATA[<span>R1C3</span>]]></one:T>
                </one:OE>
              </one:OEChildren>
            </one:Cell>
          </one:Row>
          <one:Row>
            <one:Cell>
              <one:OEChildren>
                <one:OE>
                  <one:T><![CDATA[<span>R2C3</span>]]></one:T>
                </one:OE>
              </one:OEChildren>
            </one:Cell>
          </one:Row>
        </one:Table>
      </one:OE>
    </one:OEChildren>
  </one:Outline>
</one:Page>";
    }

    private sealed class StubPageContentProvider(string xml) : IOneNotePageContentProvider
    {
        public Task<string> GetPageContentXmlAsync(string pageId, CancellationToken cancellationToken)
        {
            return Task.FromResult(xml);
        }
    }
}
