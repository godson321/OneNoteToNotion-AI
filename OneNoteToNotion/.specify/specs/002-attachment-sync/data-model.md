# 数据模型：附件同步

## 新增 Domain 模型

### AttachmentBlock

```csharp
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
```

## 扩展模型字段

### NotionBlockInput（已存在）

新增 file block 类型支持：

```csharp
// file block 结构
{
    "type": "file",
    "file": {
        "name": "document.pdf",
        "type": "external",
        "external": {
            "url": "https://s3.us-west-2.amazonaws.com/..."
        }
    }
}
```

## Notion API 相关模型

### FileUploadResponse

```csharp
/// <summary>
/// Notion 文件上传响应
/// </summary>
public sealed class FileUploadResponse
{
    public required string FileName { get; init; }
    public required string UploadUrl { get; init; }
    public required Dictionary<string, string> UploadHeaders { get; init; }
}
```

### S3UploadResult

```csharp
/// <summary>
/// S3 上传结果
/// </summary>
public sealed class S3UploadResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? FileName { get; init; }  // 上传成功后 Notion 返回的文件名
}
```

## OneNote XML 结构

### InsertedFile 元素

```xml
<one:InsertedFile 
    pathCache="C:\Users\...\附件.pdf"
    preferredName="附件.pdf"
    size="102400"
    lastModifiedTime="2025-02-27T10:00:00.000Z"
    objectID="{GUID}">
    <one:Icon />
</one:InsertedFile>
```

### 字段映射

| OneNote 属性 | AttachmentBlock 字段 | 说明 |
|-------------|---------------------|------|
| `preferredName` | `FileName` | 文件名（优先使用） |
| `pathCache` | `PathCache` | 本地缓存路径 |
| `size` | `FileSize` | 文件大小（字节） |
| 文件扩展名推断 | `MimeType` | 根据扩展名推断 |

## 错误降级模型

### 降级文本格式

```csharp
// 文件不存在
$"[附件文件不存在: {fileName}]"

// 文件过大
$"[附件超过大小限制: {fileName} ({sizeMB:F1}MB > {limitMB}MB)]"

// 上传失败
$"[附件上传失败: {fileName} - {errorMessage}]"

// 读取失败
$"[附件读取失败: {fileName} - {errorMessage}]"
```
