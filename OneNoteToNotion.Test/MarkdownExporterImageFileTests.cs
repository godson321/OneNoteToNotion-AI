using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Parsing;
using Xunit;

namespace OneNoteToNotion.Test;

public class MarkdownExporterImageFileTests
{
    [Fact]
    public async Task Export_ImageBlock_ShouldWriteLocalImageFileAndUseRelativePath()
    {
        var provider = new StubPageContentProvider(BuildPageWithImageXml());
        var exporter = new MarkdownExporter(provider);
        var node = new OneNoteTreeNode
        {
            Id = "page-1",
            Name = "image-page",
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

            var markdownPath = Path.Combine(outputRoot, "image-page.md");
            Assert.True(File.Exists(markdownPath), $"Expected markdown file to exist: {markdownPath}");

            var markdown = File.ReadAllText(markdownPath).Replace('\\', '/');
            Assert.DoesNotContain("(data:image/", markdown);

            var imageDirectory = Path.Combine(outputRoot, "image-page_images");
            Assert.True(Directory.Exists(imageDirectory), $"Expected image directory to exist: {imageDirectory}");

            var files = Directory.GetFiles(imageDirectory);
            Assert.Single(files);

            var imageFileName = Path.GetFileName(files[0]);
            Assert.Contains($"(image-page_images/{imageFileName})", markdown);
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

    private static string BuildPageWithImageXml()
    {
        const string onePixelPngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO5x8f8AAAAASUVORK5CYII=";

        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<one:Page xmlns:one=""http://schemas.microsoft.com/office/onenote/2013/onenote"" name=""Image Page"">
  <one:Outline>
    <one:OEChildren>
      <one:OE>
        <one:Image format=""png"">
          <one:Data>{onePixelPngBase64}</one:Data>
          <one:Size width=""1"" height=""1"" caption=""sample image"" />
        </one:Image>
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
