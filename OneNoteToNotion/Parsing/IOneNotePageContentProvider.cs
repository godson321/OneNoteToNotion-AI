namespace OneNoteToNotion.Parsing;

public interface IOneNotePageContentProvider
{
    Task<string> GetPageContentXmlAsync(string pageId, CancellationToken cancellationToken);
}
