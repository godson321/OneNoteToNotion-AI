namespace OneNoteToNotion.Infrastructure;

/// <summary>
/// Notion 文件上传响应
/// </summary>
public sealed class FileUploadResponse
{
    /// <summary>
    /// Notion file_upload 对象 ID（用于 file_upload block 引用）
    /// </summary>
    public required string FileId { get; init; }

    /// <summary>
    /// Notion 记录的文件名
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// 当前上传状态（pending/uploaded/failed/expired）
    /// </summary>
    public string? Status { get; init; }
}

/// <summary>
/// 附件处理结果
/// </summary>
public sealed class AttachmentProcessingResult
{
    /// <summary>
    /// 处理后的 Notion 文件名（成功时）
    /// </summary>
    public string? NotionFileName { get; init; }

    /// <summary>
    /// 处理是否成功
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// 错误信息（失败时）
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 文件名（用于日志和降级提示）
    /// </summary>
    public required string OriginalFileName { get; init; }

    /// <summary>
    /// 文件大小（字节）
    /// </summary>
    public long FileSize { get; init; }
}
