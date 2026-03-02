namespace OneNoteToNotion.Notion;

public interface INotionApiClient
{
    Task<string> CreateChildPageAsync(string parentPageId, string title, string token, CancellationToken cancellationToken);

    Task AppendBlocksAsync(string pageId, IReadOnlyList<NotionBlockInput> blocks, string token, CancellationToken cancellationToken);

    Task ArchivePagesAsync(IEnumerable<string> pageIds, string token, CancellationToken cancellationToken);

    Task MovePageAsync(string pageId, string newParentPageId, string token, CancellationToken cancellationToken);

    /// <summary>
    /// Get all child pages (type=child_page) under the given block/page.
    /// Returns a list of (pageId, title) tuples.
    /// </summary>
    Task<List<(string Id, string Title)>> GetChildPagesAsync(string parentPageId, string token, CancellationToken cancellationToken);

    /// <summary>
    /// Upload a file to Notion.
    /// Returns the Notion file upload id, or null if upload failed.
    /// </summary>
    Task<string?> UploadFileAsync(string fileName, byte[] fileData, string token, CancellationToken cancellationToken);
}
