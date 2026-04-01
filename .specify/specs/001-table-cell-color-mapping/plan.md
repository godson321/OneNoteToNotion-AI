# 实施计划：表格单元格颜色映射模式

## 架构概览

### 涉及组件
- **UI (WinForms)**：在现有 `panelConfig` 设置区新增映射模式选择器。
- **同步选项 / 配置**：持久化用户选择并贯穿同步管线。
- **Notion 映射**：构建表格单元格 rich_text 时应用映射模式。
- **颜色映射规则**：提供背景色映射（*_background），同时保留文本色映射。

### 数据流
1. 用户在 UI 选择映射模式。
2. 设置写入本地配置，并进入 `SyncOptions`。
3. `NotionBlockMapper` 读取模式并映射：
   - 背景模式：使用 `*_background` 注释。
   - 字体模式：保持现有行为（背景→字体色）。

## 关键决策

- **默认行为**：映射为单元格背景（Notion `_background` 注释）以保留视觉语义。
- **向后兼容**：“映射为字体颜色”保持现有行为。
- **范围**：仅表格单元格背景；文字高亮另行设置。
- **空单元格**：插入不可见占位以承载背景色（如零宽空格或 NBSP）。

## 实施步骤

1. **新增映射模式类型**
   - 创建 `TableCellColorMappingMode` 枚举：`Background` / `Text`。
   - 扩展 `SyncOptions` 增加该设置，默认 `Background`。

2. **持久化用户设置**
   - 扩展本地配置模型保存枚举值。
   - 读取：缺失时默认 `Background`。
   - 保存：序列化用户选择。

3. **更新 UI**
   - 在 `panelConfig` 添加 `Label` + `ComboBox`。
   - 选项：“映射为单元格背景” / “映射为字体颜色”。
   - 变更时更新配置与当前 `SyncOptions`。

4. **更新 Notion 映射**
   - 在 `NotionBlockMapper` 中使用 `TableBlock.CellRows` 保留单元格背景元数据。
   - `Background`：背景优先于前景色，映射到 Notion `*_background`。
   - `Text`：保持现有行为（背景→字体色）。

5. **颜色映射规则**
   - 增加背景色映射辅助：
     - `MapColor(...)` 返回基础颜色名。
     - `MapBackgroundColor(...)` 返回 `${color}_background` 或 `default`。

## 风险与缓解

- **Notion 颜色兼容性**：验证支持的 `*_background` 值，不支持则回退 `default`。
- **空单元格展示**：若零宽空格不显示背景，则改用 NBSP。
- **回归风险**：确保字体模式行为保持不变，并覆盖验证。

## 验证策略

- 手动同步验证：
  - 表头背景
  - 数据行背景
  - 前景+背景混合
  - 空单元格背景
- 切换设置后重新同步，确认模式切换生效。
- 验证 Markdown 导出无变化。

## 私有 API 范围约束（新增）

- 私有 API 清单与决策见：`.specify/specs/001-table-cell-color-mapping/private-api-notes.md`
- 本次实现只使用：`/api/v3/saveTransactionsFanout`
- 其他私有 API（上传、记录读取、签名 URL、任务入队、页面分块）留待后续阶段
