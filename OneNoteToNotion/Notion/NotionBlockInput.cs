namespace OneNoteToNotion.Notion;

public sealed class NotionBlockInput
{
    public required string Type { get; init; }

    public required object Value { get; init; }

    public List<NotionBlockInput> Children { get; init; } = new();

    // Side-channel metadata for private table background enhancement.
    // Not serialized to Notion public API payload.
    public IReadOnlyList<string?>? TableRowBackgroundColors { get; init; }
}
