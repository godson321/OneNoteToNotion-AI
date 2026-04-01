# 私有 API 记录（Notion）

更新日期：2026-03-04

## 已识别的私有 API

| API | 用途 | 本次是否使用 |
|---|---|---|
| `/api/v3/saveTransactionsFanout` | 事务写入块属性（如单元格 `format.block_color`） | 是 |
| `/api/v3/getUploadFileUrl` | 获取上传 URL（媒体上传前置） | 否（未来） |
| `/api/v3/getRecordValues` | 读取 block/page 记录 | 否（未来） |
| `/api/v3/syncRecordValues` | 同步记录值 | 否（未来） |
| `/api/v3/getSignedFileUrls` | 获取文件签名下载地址 | 否（未来） |
| `/api/v3/enqueueTask` | 任务入队 | 否（未来） |
| `/api/v3/loadPageChunk`* | 分块拉取页面树 | 否（未来） |

\* `loadPageChunk` 以抓包路径为准（不同版本可能表现为方法名或具体路径）。

## 本次范围决策

1. 仅接入 `/api/v3/saveTransactionsFanout` 实现“单元格整格背景色”同步。
2. 其余私有 API 仅记录，不在本次实现范围内。
3. 现有公开 `v1` API 流程保留，作为默认/回退路径。

## 本地配置键（本次实现）

可通过本地配置文件 `config.json`（`%LOCALAPPDATA%\\OneNoteToNotion\\config.json`）或环境变量提供：

- `privateApiTokenV2`（或环境变量 `NOTION_TOKEN_V2`）
- `privateApiSpaceId`（或环境变量 `NOTION_PRIVATE_SPACE_ID`）
- `privateApiUserId`（或环境变量 `NOTION_PRIVATE_USER_ID`，可选）
