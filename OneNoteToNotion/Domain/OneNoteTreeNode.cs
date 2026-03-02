namespace OneNoteToNotion.Domain;

public sealed class OneNoteTreeNode
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required OneNoteNodeType NodeType { get; init; }

    public string? Path { get; init; }

    public List<OneNoteTreeNode> Children { get; } = new();
}
