namespace OneNoteToNotion.Domain;

public sealed class SemanticDocument
{
    public string Title { get; init; } = string.Empty;

    public List<SemanticBlock> Blocks { get; } = new();
}

public abstract record SemanticBlock;

public sealed record HeadingBlock(int Level, string Text, IReadOnlyList<TextRun>? Runs = null) : SemanticBlock;

public sealed record ParagraphBlock(
    IReadOnlyList<TextRun> Runs,
    ParagraphAlignment Alignment = ParagraphAlignment.Left,
    LayoutHint Layout = LayoutHint.Normal,
    int IndentLevel = 0) : SemanticBlock;

public sealed record BulletedListBlock(IReadOnlyList<IReadOnlyList<TextRun>> Items) : SemanticBlock;

public sealed record NumberedListBlock(IReadOnlyList<IReadOnlyList<TextRun>> Items) : SemanticBlock;

public sealed record TableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<TextRun>>> Rows) : SemanticBlock;

public sealed record ImageBlock(
    string DataUri,
    string Caption,
    string OriginalFormat = "",
    int OriginalSize = 0,
    int FinalSize = 0,
    bool IsProcessed = false,
    string? ProcessingError = null,
    int Width = 0,
    int Height = 0,
    string? NotionFileId = null,
    string? SyncError = null) : SemanticBlock;

/// <summary>
/// 附件块 - 表示 OneNote 中的文件附件
/// </summary>
public sealed record AttachmentBlock(
    string FileName,              // 文件名（含扩展名）
    string? PathCache,            // 本地文件路径（可能为 null）
    string? FileDataBase64,       // base64 编码的文件数据（小文件）
    string? MimeType,             // MIME 类型
    long FileSize,                // 文件大小（字节）
    string? NotionFileName = null,  // Notion 返回的文件名（上传后填充）
    string? ErrorMessage = null     // 处理错误信息
) : SemanticBlock;

public sealed record UnsupportedBlock(string Kind, string Raw, string DegradeTo) : SemanticBlock;

public sealed record TextRun(
    string Text,
    TextStyleStyle Style,
    string? Link = null);

public sealed record TextStyleStyle(
    bool Bold = false,
    bool Italic = false,
    bool Underline = false,
    bool Strikethrough = false,
    bool Code = false,
    string? ForegroundColor = null,
    string? BackgroundColor = null);

public enum ParagraphAlignment
{
    Left,
    Center,
    Right,
    Justify
}

public enum LayoutHint
{
    Normal,
    AbsolutePositioned,
    ColumnLike,
    FloatingObject
}
