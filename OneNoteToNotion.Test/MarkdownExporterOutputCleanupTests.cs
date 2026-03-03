using OneNoteToNotion.Domain;
using OneNoteToNotion.Infrastructure;
using OneNoteToNotion.Parsing;
using Xunit;

namespace OneNoteToNotion.Test;

public class MarkdownExporterOutputCleanupTests
{
    [Fact]
    public async Task Export_ShouldPreserveDotDirectories_WhenCleaningOutputFolder()
    {
        var provider = new StubPageContentProvider(BuildSimplePageXml());
        var exporter = new MarkdownExporter(provider);
        var node = new OneNoteTreeNode
        {
            Id = "page-1",
            Name = "cleanup-page",
            NodeType = OneNoteNodeType.Page
        };

        var outputRoot = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "TempExport",
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(outputRoot);

            var dotDirectory = Path.Combine(outputRoot, ".obsidian");
            Directory.CreateDirectory(dotDirectory);
            var dotFile = Path.Combine(dotDirectory, "config");
            File.WriteAllText(dotFile, "keep");

            var regularDirectory = Path.Combine(outputRoot, "old-folder");
            Directory.CreateDirectory(regularDirectory);
            File.WriteAllText(Path.Combine(regularDirectory, "old.txt"), "remove");
            File.WriteAllText(Path.Combine(outputRoot, "old.md"), "remove");

            var result = await exporter.ExportAsync([node], outputRoot, cancellationToken: CancellationToken.None);
            Assert.Equal(1, result.ExportedFiles);

            Assert.True(Directory.Exists(dotDirectory), $"Expected dot directory to remain: {dotDirectory}");
            Assert.True(File.Exists(dotFile), $"Expected file under dot directory to remain: {dotFile}");

            Assert.False(Directory.Exists(regularDirectory), $"Expected regular directory to be deleted: {regularDirectory}");
            Assert.False(File.Exists(Path.Combine(outputRoot, "old.md")), "Expected regular file to be deleted.");

            var markdownPath = Path.Combine(outputRoot, "cleanup-page.md");
            Assert.True(File.Exists(markdownPath), $"Expected exported markdown file: {markdownPath}");
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

    private static string BuildSimplePageXml()
    {
        return @"<?xml version=""1.0"" encoding=""utf-8""?>
<one:Page xmlns:one=""http://schemas.microsoft.com/office/onenote/2013/onenote"" name=""Simple Page"">
  <one:Outline>
    <one:OEChildren>
      <one:OE>
        <one:T><![CDATA[<span>hello</span>]]></one:T>
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
