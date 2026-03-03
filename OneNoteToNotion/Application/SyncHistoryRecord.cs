namespace OneNoteToNotion.Application;

public sealed class SyncHistoryRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public DateTime StartedAt { get; init; }

    public DateTime FinishedAt { get; init; }

    public string Operation { get; init; } = "sync";

    public string Status { get; init; } = "success";

    public bool DryRun { get; init; }

    public int CreatedPages { get; init; }

    public int SyncedContentPages { get; init; }

    public int FailedCount { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string? TopLevelError { get; init; }

    public List<SyncHistoryFailedItem> FailedItems { get; init; } = new();

    public List<string> LogEntries { get; init; } = new();
}

public sealed class SyncHistoryFailedItem
{
    public string NodeId { get; init; } = string.Empty;

    public string NodeName { get; init; } = string.Empty;

    public string NodeType { get; init; } = string.Empty;

    public string ParentPageId { get; init; } = string.Empty;

    public string ErrorMessage { get; init; } = string.Empty;
}
