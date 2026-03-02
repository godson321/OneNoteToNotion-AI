using OneNoteToNotion.Domain;
using OneNoteToNotion.Notion;

namespace OneNoteToNotion.Mapping;

public interface INotionBlockMapper
{
    IReadOnlyList<NotionBlockInput> Map(SemanticDocument semanticDocument);
}
