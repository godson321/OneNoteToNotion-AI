# 实现任务：图片同步支持

## Phase 1: 基础设施

- [x] 1.1 添加 System.Drawing.Common NuGet 包
  - 编辑 `OneNoteToNotion.csproj`
  - 添加 `<PackageReference Include="System.Drawing.Common" Version="*" />`
  - 恢复 NuGet 包验证安装成功
  - **依赖**: 无
  - **需求**: 故事 2

- [x] 1.2 创建 Infrastructure/ImageResizer.cs 文件
  - 创建 `ImageProcessingResult` 类
  - 创建 `ImageProcessingConstants` 常量类
  - 定义 `ProcessImage` 方法签名
  - **依赖**: 1.1
  - **需求**: 故事 2

## Phase 2: 图片处理核心

- [x] 2.1 实现 base64 解码和图片加载
  - 实现 `TryDecodeFromBase64` 方法
  - 处理无效 base64 格式
  - 处理损坏的图片数据
  - 返回 `ImageProcessingResult`
  - **依赖**: 1.2
  - **需求**: 故事 1

- [x] 2.2 实现 JPEG 格式转换（质量 80%）
  - 使用 `System.Drawing.Imaging.Encoder.Quality`
  - 设置质量参数为 80
  - 保存为 JPEG 格式到 MemoryStream
  - 转换回 base64
  - **依赖**: 2.1
  - **需求**: 故事 2

- [x] 2.3 实现大小检查和自动缩图
  - 检查 base64 编码后大小
  - 超过 5MB 时触发缩图逻辑
  - 每次缩小 20%，最多迭代 10 次
  - 保持宽高比
  - **依赖**: 2.2
  - **需求**: 故事 2

- [x] 2.4 [P] 实现 Data URI 生成
  - 格式: `data:image/jpeg;base64,{data}`
  - 确保格式正确
  - 与 `ImageBlock.DataUri` 兼容
  - **依赖**: 2.2
  - **需求**: 故事 3

## Phase 3: 解析器扩展

- [x] 3.1 在 OneNoteXmlSemanticParser 中添加图片检测
  - 在 `ProcessOEChildren` 方法中检测 `<one:Image>` 元素
  - 在检测到 Table 之前检测 Image
  - **依赖**: 1.1
  - **需求**: 故事 1

- [x] 3.2 实现图片元素解析
  - 创建 `ParseImage` 私有方法
  - 提取 `format` 属性
  - 提取 `<one:Data>` 内容
  - 提取 `<one:Size>` 信息
  - 调用 `ImageResizer.ProcessImage`
  - 创建 `ImageBlock` 添加到 blocks
  - **依赖**: 2.3, 3.1
  - **需求**: 故事 1
  - **完成备注**: 已在 `ParseImage` 中新增 `ExtractImageSize`，从 `<one:Size>` 结构化提取宽高（缺失/非数字回退 `0`），并写入 `ImageBlock.Width/Height`。

- [x] 3.3 添加图片解析诊断输出
  - 在 `DumpDiagnostics` 方法中添加 ImageBlock 处理
  - 输出原始格式、原始大小、处理后大小
  - **依赖**: 3.2
  - **需求**: 故事 4
  - **完成备注**: 已输出 `Format` / `OriginalSize` / `FinalSize`，并保留 `Caption`、`ProcessingError`、`DataUriLength` 便于排查。

## Phase 4: 映射器扩展

- [x] 4.1 在 NotionBlockMapper 中添加 ImageBlock case
  - 在 `Map` 方法的 switch 中添加 `case ImageBlock image:`
  - 构造 Notion image block 结构
  - 使用 Data URI 作为图片源
  - **依赖**: 2.4
  - **需求**: 故事 3

## Phase 5: 错误处理

- [x] 5.1 实现图片解析失败降级
  - 捕获图片解析异常
  - 创建 `UnsupportedBlock` 作为降级
  - 记录警告日志
  - **依赖**: 3.2
  - **需求**: 故事 4

- [x] 5.2 实现图片处理失败降级
  - 在 `ImageResizer.ProcessImage` 中返回失败结果
  - 解析器检查 `Success` 标志
  - 失败时插入占位符文本
  - **依赖**: 5.1
  - **需求**: 故事 4
  - **完成备注**: 已将 `ImageResizer` 中"压缩后仍超限"改为 `Success=false`，解析器在 `Success=false` 时统一降级为 `UnsupportedBlock("Image", ..., "图片处理失败: ...")`。

- [x] 5.3 添加错误日志记录
  - 记录图片处理失败原因
  - 记录原始大小和格式
  - 输出到同步日志
  - **依赖**: 5.2
  - **需求**: 故事 4
  - **完成备注**: 已在解析层与映射层输出失败/告警日志，包含 `format`、`originalSize`、`finalSize` 与失败原因字段。

## Phase 6: 集成测试

- [x] 6.1 准备测试数据
  - 小图片 OneNote 页面（< 1MB）
  - 中等图片页面（1-5MB）
  - 大图片页面（> 5MB）
  - 混合内容页面（文本+图片+表格）
  - **依赖**: 所有实现任务
  - **需求**: 所有
  - **完成备注**: 已创建最小可复现样例：`tests/ImageSyncFixtures/small-image.xml`（小图）、`large-image.xml`（超限图）、`invalid-image.xml`（坏 base64 + 空 Data）。

- [ ] 6.2 执行端到端测试
  - 同步测试页面到 Notion
  - 验证图片显示正确
  - 验证缩图效果
  - 验证降级处理
  - **依赖**: 6.1
  - **需求**: 所有

- [ ] 6.3 验证诊断输出
  - 检查 semantic.txt 包含图片信息
  - 验证日志包含处理结果
  - **依赖**: 6.2
  - **需求**: 故事 4

## 完成标准

- [ ] 所有任务标记完成
- [ ] 单元测试通过
- [ ] 端到端测试通过
- [ ] 代码审查完成
- [ ] 诊断输出验证通过

## 任务图例

- `[P]` - 可与其他 [P] 任务并行执行
- **依赖** - 必须先完成的任务编号
- **需求** - 对应的用户故事编号
