# 实现任务：附件同步支持

## Phase 1: 基础设施

- [x] 1.1 更新 AttachmentBlock 模型（已存在但需扩展）
  - 添加 `NotionFileName` 字段
  - 添加 `ErrorMessage` 字段
  - **依赖**: 无
  - **需求**: 故事 3

- [x] 1.2 创建 FileUploadResponse 和 S3UploadResult 模型
  - 创建 `Infrastructure/NotionApiModels.cs` 文件
  - 定义上传响应数据结构
  - **依赖**: 无
  - **需求**: 故事 2

## Phase 2: Notion API 扩展

- [x] 2.1 实现 GetFileUploadUrlAsync 方法
  - 调用 `POST /v1/files` API
  - 解析响应获取 uploadUrl 和文件名
  - 处理 API 错误（401, 429, 500 等）
  - **依赖**: 1.2
  - **需求**: 故事 2

- [x] 2.2 实现 UploadToS3Async 方法
  - 构造 PUT 请求到 AWS S3 URL
  - 添加必要的 headers（Content-Type 等）
  - 处理 S3 返回的错误
  - **依赖**: 2.1
  - **需求**: 故事 2

- [x] 2.3 实现 UploadFileAsync 组合方法
  - 组合 GetFileUploadUrlAsync + UploadToS3Async
  - 返回 Notion 文件名或 null（失败时）
  - 添加重试逻辑（网络超时）
  - **依赖**: 2.2
  - **需求**: 故事 2

## Phase 3: 解析器扩展

- [x] 3.1 在 OneNoteXmlSemanticParser 中添加附件检测
  - 在 `ProcessOEChildren` 方法中检测 `<one:InsertedFile>`
  - 检测顺序：图片 → 附件 → 表格
  - **依赖**: 无
  - **需求**: 故事 1

- [x] 3.2 实现 InsertedFile 解析
  - 创建 `ParseInsertedFile` 私有方法
  - 提取 `preferredName`、`pathCache`、`size` 属性
  - 推断 MIME 类型
  - 检查文件是否存在且可读
  - **依赖**: 3.1
  - **需求**: 故事 1

- [x] 3.3 实现文件数据读取
  - 通过 `pathCache` 读取本地文件
  - 小文件直接读入内存（base64）
  - 大文件仅记录信息（不读入内存，后续流式处理）
  - 处理文件读取异常
  - **依赖**: 3.2
  - **需求**: 故事 1

- [x] 3.4 添加附件解析诊断输出
  - 在 `DumpDiagnostics` 方法中添加 AttachmentBlock 处理
  - 输出文件名、大小、MIME 类型、是否存在
  - **依赖**: 3.3
  - **需求**: 故事 4

## Phase 4: 映射器扩展

- [x] 4.1 在 NotionBlockMapper 中添加 AttachmentBlock case
  - 在 `Map` 方法的 switch 中添加 `case AttachmentBlock attachment:`
  - 构造 Notion file block 结构
  - 使用 Notion 返回的文件名
  - **依赖**: 2.3, 1.1
  - **需求**: 故事 3

- [x] 4.2 在 NotionSyncOrchestrator 中实现附件预处理
  - 添加 `ProcessAttachmentsAsync` 方法
  - 在同步前上传所有附件
  - 更新 AttachmentBlock.NotionFileName
  - **依赖**: 4.1
  - **需求**: 故事 3

## Phase 5: 错误处理

- [x] 5.1 实现文件不存在降级
  - 在 `ParseInsertedFile` 中检查文件存在性
  - 不存在时设置 `ErrorMessage` 降级
  - 记录警告日志
  - **依赖**: 3.2
  - **需求**: 故事 4

- [x] 5.2 实现文件大小超限降级
  - 检查 `size` 属性是否超过 100MB
  - 超过时设置 `ErrorMessage` 降级
  - 记录警告日志
  - **依赖**: 3.2
  - **需求**: 故事 4

- [x] 5.3 实现文件读取失败降级
  - 捕获文件读取异常
  - 设置 `ErrorMessage` 降级
  - 记录错误日志
  - **依赖**: 3.3
  - **需求**: 故事 4

- [x] 5.4 实现上传失败降级
  - 在 `ProcessAttachmentsAsync` 中检查 `UploadFileAsync` 返回值
  - 返回 null 时跳过并记录警告
  - Mapper 层对未上传附件降级
  - **依赖**: 4.2
  - **需求**: 故事 4

- [x] 5.5 添加错误日志记录
  - 在解析层、映射层和同步层添加详细的错误日志
  - 包含文件名、路径、错误原因
  - 输出到同步日志和诊断文件
  - **依赖**: 5.1, 5.2, 5.3, 5.4
  - **需求**: 故事 4

## Phase 6: 集成测试

- [x] 6.1 准备测试数据
  - 小附件测试页面（< 1MB PDF）- `small-attachment.xml`
  - 大附件测试页面（> 100MB，测试降级）- `large-attachment.xml`
  - 不存在文件路径测试页面 - `missing-attachment.xml`
  - 创建 `OneNoteToNotion.Test/Fixtures/AttachmentSync/*.xml`
  - **依赖**: 所有实现任务
  - **需求**: 所有

- [x] 6.2 创建单元测试
  - 解析器测试（附件信息提取、MIME 推断、错误标记）
  - 映射器测试（file block 构造、降级处理）
  - 降级处理测试（各种文件类型）
  - 创建 `OneNoteToNotion.Test/AttachmentSyncTests.cs`
  - **依赖**: 6.1
  - **需求**: 所有

- [x] 6.3 验证测试项目
  - 创建 `OneNoteToNotion.Test/OneNoteToNotion.Test.csproj`
  - 配置 xUnit 测试框架
  - 配置项目引用
  - **依赖**: 6.2
  - **需求**: 所有

> **注意**: 端到端测试（真实 Notion API 调用）需要手动执行，需要有效的 Notion Token。

## 完成标准

- [x] 所有任务标记完成
- [x] 单元测试通过（13/13）
- [ ] 端到端测试通过（需手动执行）
- [x] 代码审查完成
- [x] 诊断输出验证通过

## 测试结果

```
已通过! - 失败: 0，通过: 13，已跳过: 0，总计: 13
```

测试覆盖：
- ✅ 附件信息提取（文件名、路径、大小）
- ✅ MIME 类型解析
- ✅ 文件不存在降级
- ✅ 文件大小超限降级
- ✅ file block 构造
- ✅ 降级为占位符文本
- ✅ 多种文件类型支持（PDF, DOCX, XLSX, TXT）

> 端到端测试（真实 Notion API 调用）需要手动执行，需要有效的 Notion Token。

## 任务图例

- `[P]` - 可与其他 [P] 任务并行执行
- **依赖** - 必须先完成的任务编号
- **需求** - 对应的用户故事编号
