# AGENTS.md

## 全局规则

### AI 行为偏好
- 回答必须使用中文，每次回复前都要说 “你好MOSS，我是启示录”
- 如果当前使用了skills，必须把正在用的skills的名字输出，然后再输出正常内容
- 对总结、Plan、Task、以及长内容的输出，优先进行逻辑整理后使用美观的Table格式整齐输出;普通内容正常输出

### 编码与终端
- 路径含空格时必须加引号。

### Session启动提醒
- 每次新session请先执行：serena.activate_project（必要时用脚本复制命令片段）
- 每次新session请先加载 using-superpowers 技能，确保遵循技能使用规范（在任何响应前检查是否有适用的技能）
- 若“本项目规则”存在会话启动覆盖条款，则以本项目规则为准

### 工具使用
1. 文件与代码检索：使用serena mcp来进行文件与代码的检索
2. 文件相关操作：对文件的创建、读取、编辑、删除等操作
- 优先使用apply_patch工具进行
- 读文件，apply_patch工具报错或出现问题的情况下使用desktop-commander mcp
- 任何情况下，禁止使用cmd、powershell或者python来进行文件相关操作
- MCP强制门禁：
  - 任何“新需求/改代码/查问题”任务，必须先使用serena MCP做检索定位（如 `list_dir`、`find_symbol`、`search_for_pattern`），再进行修改。
  - 未完成MCP检索定位前，禁止直接进入代码实现。
  - 每次完成一轮实现后，回复中必须明确说明本轮使用了哪些MCP操作；若因客观原因未使用，必须先说明原因并获得用户确认。

### 记忆写入规则
- 当用户明确说“让你记住规则/要记住规则/写入规则”时，必须把该事项写入本项目 `AGENTS.md`

### Spec-Kit 触发规则（全局）
- 当对话中明确提出“做新内容 / 新功能 / 新需求 / 新特性”时，必须先启动 `spec-kit-skill`。
- 启动后必须先检查 `.specify` 目录；若不存在，必须先初始化 Spec-Kit，再进入后续阶段。
- Specify 阶段除了文字需求，必须同步审阅原型图/截图/附件图片，并输出“文字需求 vs 视觉设计”对齐清单。
- 必须按 Spec-Kit 7 阶段工作流逐步执行，不可跳步：
  1. Constitution
  2. Specify
  3. Clarify
  4. Plan
  5. Tasks
  6. Analyze
  7. Implement
- 每一阶段都要有阶段产出，并在进入下一阶段前与用户对齐确认。
- 若项目尚未初始化 Spec-Kit（如缺少 `.specify` 目录），先执行初始化流程，再进入 7 阶段。
- 强制门禁：未完成并落盘前 6 阶段（Constitution/Specify/Clarify/Plan/Tasks/Analyze）产出前，禁止进入代码实现（Implement）。
- Clarify 阶段必须做二次确认：至少明确“页面/交互是否按原型图还原、字段是否 1:1、是否允许模板化简化”后，才能进入 Plan。
- 每次进入 Implement 前，必须先向用户明确给出 Spec 产物路径（如 `.specify/...`）并获得继续确认。
- Implement 阶段开始前与结束后都必须确认：开始前确认“按设计实现”，结束后确认“实现结果与设计一致”。
- 禁止先实现后设计；若发现已实现与设计不一致，必须立即停止继续开发，回到 Specify/Clarify 重新对齐。

### Spec-Kit + Everything-Claude-Code 组合流程（全局强制）
- 新任务默认采用“Spec-Kit 落盘 + ECC 执行”双轨流程，禁止只在对话中口头推进。
- **强制前置门禁**：不允许直接使用任何 `everything-claude-code:*` 命令；必须先完成 `spec-kit-skill` 前置阶段并落盘。
- 强制执行顺序（不可跳步）：
  1. `spec-kit-skill`：完成并落盘 Constitution/Specify/Clarify/Plan/Tasks/Analyze（前 6 阶段）。
  2. 仅在第 1 步完成后，才允许执行 `/everything-claude-code:plan`（输出工程实现计划：风险、依赖、分阶段）。
  3. 用户明确回复“继续/同意”后，必须立刻执行 `/everything-claude-code:tdd`（先写失败测试，再实现）。
  4. 实现后必须执行 `/everything-claude-code:verification-loop`。
  5. 验证后必须执行 `/everything-claude-code:security-review`。
  6. 最后必须回写 `.specify/specs/<feature>/tasks.md` 与 `analysis.md` 的完成状态和剩余风险。
- 进入 Implement 前，必须给出本次 feature 的规范文件路径并获得确认（如 `.specify/specs/001-xxx/`）。
- 若任一步骤无法执行，必须立即停止并说明阻塞原因，等待用户确认后再继续。
- 每次新会话启动新需求时，默认先执行 `spec-kit-skill`，完成前 6 阶段后才可执行：`/everything-claude-code:plan <需求>`。

### 快捷口令（推荐）
- 用户只要说：`启示录`，即进入“Spec-Kit + ECC 锁流程”**准备态**（先完成前置检查与执行准备）。
- 用户说：`启示录来看这个：<需求>` | `启示录，+任意文字 <需求>` | `启示录：<需求>`，即进入“Spec-Kit + ECC 锁流程”**执行态**。
- 执行态等价执行规则：
  1. 先执行 `spec-kit-skill` 并完成前 6 阶段落盘（Constitution/Specify/Clarify/Plan/Tasks/Analyze）。
  2. 再执行 `/everything-claude-code:plan <需求>`。
  3. 用户回复“继续/同意”后，立即执行 `/everything-claude-code:tdd`。
  4. 然后执行 `/everything-claude-code:verification-loop` 与 `/everything-claude-code:security-review`。
  5. 最后回写 `.specify/specs/<feature>/tasks.md` 与 `analysis.md`。
- 轻量优化快速通道：若仅为规则文案微调/小范围配置优化（不涉及功能开发与代码实现），可跳过 Spec-Kit，直接修改并在回复中说明变更点。

### 每次新对话开场模板（可直接复制一行）
- `本任务强制使用 Spec-Kit + Everything-Claude-Code 流程：先运行 spec-kit-skill 并完成前6阶段落盘（Constitution/Specify/Clarify/Plan/Tasks/Analyze），再执行 /everything-claude-code:plan；我回复“继续”后必须立即 /everything-claude-code:tdd，随后 /everything-claude-code:verification-loop 与 /everything-claude-code:security-review，最后回写 .specify/specs/<feature>/tasks.md 与 analysis.md。`

### 违反门禁时的固定提示文案（统一输出）
- 当检测到未先完成 Spec-Kit 前 6 阶段就尝试执行 `everything-claude-code:*` 时，必须先输出以下固定文案并停止执行：
- `【流程门禁拦截】当前请求涉及 everything-claude-code，但尚未完成 Spec-Kit 前置阶段（Constitution/Specify/Clarify/Plan/Tasks/Analyze）并落盘。根据项目全局规则，现已停止执行。请先完成 Spec-Kit 前置阶段，确认产物路径后我再继续。`
