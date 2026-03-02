namespace OneNoteToNotion.Notion;

public sealed class NotionBlockInput
{
    public required string Type { get; init; }

    public required object Value { get; init; }

    public List<NotionBlockInput> Children { get; init; } = new();
}
