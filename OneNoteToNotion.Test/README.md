# OneNoteToNotion.Test - 测试项目

## 项目结构

```
OneNoteToNotion.Test/
├── OneNoteToNotion.Test.csproj    # 测试项目配置
├── AttachmentSyncTests.cs         # 附件同步测试
├── README.md                      # 本文件
└── Fixtures/
    └── AttachmentSync/
        ├── small-attachment.xml   # 小附件测试数据 (< 1MB)
        ├── large-attachment.xml   # 大附件测试数据 (> 100MB, 测试降级)
        └── missing-attachment.xml # 缺失附件测试数据 (测试降级)
```

## 测试覆盖

### AttachmentSyncTests 测试类

| 测试方法 | 说明 | 阶段 |
|---------|------|------|
| `Parse_SmallAttachment_ShouldExtractFileInfo` | 验证小附件信息提取 | Phase 3 |
| `Parse_LargeAttachment_ShouldFlagSizeError` | 验证大附件大小检查 | Phase 3 |
| `Parse_MissingAttachment_ShouldFlagNotFoundError` | 验证文件不存在降级 | Phase 3 |
| `Parse_Attachment_ShouldInferMimeType` | 验证 MIME 类型推断 | Phase 3 |
| `Map_AttachmentWithNotionFileName_ShouldCreateFileBlock` | 验证 file block 构造 | Phase 4 |
| `Map_AttachmentWithError_ShouldCreateFallbackBlock` | 验证降级处理 | Phase 4 |
| `Map_AttachmentWithoutNotionFileName_ShouldCreateFallbackBlock` | 验证未上传降级 | Phase 4 |
| `Parse_VariousFileTypes_ShouldInferCorrectMimeType` | 验证多种文件类型 | Phase 5 |

## 运行测试

### 前置条件

由于主项目依赖 OneNote COM Interop，测试项目需要以下任一方式运行：

#### 方式 1：在已安装 OneNote 的 Windows 环境

```bash
cd OneNoteToNotion.Test
dotnet test
```

#### 方式 2：使用 Mock 模式（推荐用于 CI）

需要修改 `OneNoteInteropHierarchyProvider` 使用 Mock 实现，或创建测试专用的接口实现。

### 测试结果

预期所有测试通过，验证：

1. ✅ 附件 XML 解析正确
2. ✅ 文件信息提取完整（文件名、路径、大小、MIME）
3. ✅ 错误场景降级处理（不存在、超大）
4. ✅ 映射到 Notion block 正确
5. ✅ 降级为占位符文本正确

## 端到端测试

端到端测试（真实 Notion API 调用）需要：

1. 有效的 Notion Integration Token
2. 测试用的 Notion 页面 ID
3. 实际的 OneNote 页面包含附件

### 手动测试步骤

1. 启动 OneNoteToNotion 应用程序
2. 选择包含附件的 OneNote 页面
3. 输入 Notion Token 和父页面 ID
4. 执行同步
5. 验证 Notion 页面中：
   - 小附件显示为可下载的 file block
   - 大附件降级为文本提示
   - 缺失附件降级为文本提示

## 已知问题

### OneNote COM 依赖

- 主项目依赖 `Interop.Microsoft.Office.Interop.OneNote`
- 在测试环境中可能无法编译
- 解决方案：使用 `UnavailableOneNoteProvider` 替代或安装 OneNote

### 测试数据

测试数据使用虚拟文件路径（`C:\Test\...`），实际运行时：
- 单元测试：不依赖真实文件，仅测试 XML 解析
- 集成测试：需要准备真实文件到对应路径
