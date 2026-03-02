using OneNoteToNotion.Domain;
using OneNoteToNotion.Parsing;

namespace OneNoteToNotion.Infrastructure;

public sealed class UnavailableOneNoteProvider : IOneNoteHierarchyProvider, IOneNotePageContentProvider
{
    private readonly string _reason;

    public UnavailableOneNoteProvider(string reason)
    {
        _reason = string.IsNullOrWhiteSpace(reason) ? "OneNote 不可用。" : reason;
    }

    public Task<IReadOnlyList<OneNoteTreeNode>> GetNotebookHierarchyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(_reason);
    }

    public Task<string> GetPageContentXmlAsync(string pageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(_reason);
    }
}
