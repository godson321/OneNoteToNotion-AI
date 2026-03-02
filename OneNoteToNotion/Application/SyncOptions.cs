namespace OneNoteToNotion.Application;

public sealed record SyncOptions(
    string NotionToken,
    string ParentPageId,
    bool DryRun);
