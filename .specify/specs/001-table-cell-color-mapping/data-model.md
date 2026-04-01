# 数据模型：表格单元格颜色映射模式

## 新增类型

```csharp
public enum TableCellColorMappingMode
{
    Background,
    Text
}
```

## 配置/选项新增

```csharp
public sealed record SyncOptions(
    string NotionToken,
    string ParentPageId,
    bool DryRun,
    TableCellColorMappingMode TableCellColorMappingMode = TableCellColorMappingMode.Background
);
```

## 持久化

- 本地配置需要保存该枚举值（字符串或整数）。
- 缺失时默认 `Background` 以保证向后兼容。

## 映射行为摘要

- `Background`：背景色映射到 Notion `*_background` 注释。
- `Text`：背景色映射为 Notion 字体颜色（现有行为）。
