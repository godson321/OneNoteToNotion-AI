using OneNoteToNotion.Domain;

namespace OneNoteToNotion.Parsing;

public interface IOneNoteHierarchyProvider
{
    Task<IReadOnlyList<OneNoteTreeNode>> GetNotebookHierarchyAsync(CancellationToken cancellationToken);
}
