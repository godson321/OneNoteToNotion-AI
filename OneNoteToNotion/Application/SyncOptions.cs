namespace OneNoteToNotion.Application;

public sealed record SyncOptions(
    string NotionToken,
    string ParentPageId,
    bool DryRun,
    OneNoteToNotion.Domain.TableCellColorMappingMode TableCellColorMappingMode =
        OneNoteToNotion.Domain.TableCellColorMappingMode.Background);
