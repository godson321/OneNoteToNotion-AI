using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Parsing;
using Xunit;

namespace OneNoteToNotion.Test;

public class MarkdownExporterAttachmentFileTests
{
    [Fact]
    public async Task Export_AttachmentBlock_ShouldWriteLocalAttachmentAndUseRelativePath()
    {
        var provider = new StubPageContentProvider(BuildPageWithAttachmentXml());
        var exporter = new MarkdownExporter(provider);
        var node = new OneNoteTreeNode
        {
            Id = "page-1",
            Name = "attachment-page",
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

            var markdownPath = Path.Combine(outputRoot, "attachment-page.md");
            Assert.True(File.Exists(markdownPath), $"Expected markdown file to exist: {markdownPath}");

            var markdown = File.ReadAllText(markdownPath).Replace('\\', '/');
            Assert.DoesNotContain("(data:", markdown);

            var attachmentDirectory = Path.Combine(outputRoot, "attachment-page_attachments");
            Assert.True(Directory.Exists(attachmentDirectory), $"Expected attachment directory to exist: {attachmentDirectory}");

            var files = Directory.GetFiles(attachmentDirectory);
            Assert.Single(files);

            var attachmentFileName = Path.GetFileName(files[0]);
            Assert.Contains($"(attachment-page_attachments/{attachmentFileName})", markdown);
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

    private static string BuildPageWithAttachmentXml()
    {
        const string fileBase64 = "aGVsbG8="; // "hello"

        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<one:Page xmlns:one=""http://schemas.microsoft.com/office/onenote/2013/onenote"" name=""Attachment Page"">
  <one:Outline>
    <one:OEChildren>
      <one:OE>
        <one:InsertedFile preferredName=""readme.txt"" mimeType=""text/plain"" size=""5"">
          <one:Data>{fileBase64}</one:Data>
        </one:InsertedFile>
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
