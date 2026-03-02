using System.Collections.Concurrent;
using OneNoteToNotion.Domain;

namespace OneNoteToNotion.Application;

public sealed class SyncResult
{
    // Fields for Interlocked operations in concurrent sync
    public int CreatedPages;

    public int SyncedContentPages;

    public Dictionary<string, string> SyncedPageMap { get; } = new();

    public ConcurrentBag<string> Warnings { get; } = new();

    public ConcurrentBag<string> LogEntries { get; } = new();

    public ConcurrentBag<FailedSyncItem> FailedPages { get; } = new();

    public void Merge(SyncResult other)
    {
        Interlocked.Add(ref CreatedPages, other.CreatedPages);
        Interlocked.Add(ref SyncedContentPages, other.SyncedContentPages);

        lock (SyncedPageMap)
        {
            foreach (var pair in other.SyncedPageMap)
            {
                SyncedPageMap[pair.Key] = pair.Value;
            }
        }

        foreach (var w in other.Warnings) Warnings.Add(w);
        foreach (var l in other.LogEntries) LogEntries.Add(l);
        foreach (var f in other.FailedPages) FailedPages.Add(f);
    }
}

public sealed record FailedSyncItem(
    OneNoteTreeNode Node,
    string ParentPageId,
    string ErrorMessage);

public sealed record SyncProgress(
    int Current,
    int Total,
    string CurrentPageName);
