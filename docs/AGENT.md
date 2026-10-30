# Workspace Agent 0.2

## 执行路径

`ChatData → AgentRunner → AgentProtocol → provider → tool calls → WorkspaceTools → tool results → provider`

Claude 使用 `tools[].input_schema`、assistant `tool_use` 与紧随其后的 user `tool_result`。同一回合的多个结果放在一条 user 消息内，并携带 `is_error`。OpenAI 使用 function tools、assistant `tool_calls` 和带 `tool_call_id` 的 tool 消息。执行工具前必须收到完整、非截断响应；未知工具和错误参数不会执行额外操作。

完整工具消息组才写入内存对话检查点。中断任务下次继续时明确提示模型重新检查文件，因为磁盘修改可能已生效。切换工作区、服务、模型或密钥变量会清空上下文。Agent 不把普通聊天选区自动拼接进任务，直接读取工作区磁盘文件；应先保存文件。

每次运行的读写模式写在本次用户消息开头（system 在整个对话中不变）：智能体为可写；询问为只读；计划为只读并要求最后给出实施计划（`AgentRunner.PlanModeNote`），用户点击“按此计划执行”后在同一对话中以可写模式发送 `AgentRunner.ExecutePlanPrompt`。

## 十八个工具

| 工具 | 行为 |
| --- | --- |
| `list_directory(path)` | 列出指定目录的直接子项，最多返回 200 项。 |
| `search_files(path, query, regex?, glob?)` | 递归逐行搜索，忽略大小写：默认字面匹配，`regex: true` 时按 .NET 正则（单次匹配 200 毫秒上限，超时报错）。`glob` 限定文件（不含 `/` 时匹配文件名，否则匹配相对 `path` 的路径）。返回路径、行号与片段；最多扫描 50,000 项、32 层，每个文件最多 50 行，总计 200 行或 24,000 字符，超出时 `truncated: true`。遍历时只用目录枚举返回的属性判断链接与排除规则（不再对每个文件逐级检查上级目录），文件每 8 个一批并行读取。 |
| `find_files(path, pattern)` | 只读。按通配模式查找文件：`*` 与 `?` 不跨目录，`**` 匹配任意层；不含 `/` 时匹配文件名。最多 500 个，按路径排序；受同样的排除与忽略规则约束。 |
| `read_file(path, start_line?, line_count?)` | 读取文本文件，返回全文（或指定行段）、编码名称、总行数与整个文件的 SHA-256。`content` 每行为“行号 + 制表符 + 内容”（0.7.0），超过 2,000 字符的行截短并注明（此时按部分读取处理）。超过 2000 行的文件不带行段时只返回前 2000 行并提示分段读取；只读过一部分的文件不能用 `write_file` 整体覆盖，只能用 `edit_file`。超过单文件上限（默认 1 MiB）的文件只能分段读取（最大 32 MiB，每段最多 5000 行）。文件在编辑器中有未保存修改时返回编辑器中的内容并标记 `unsaved_editor_changes`，此时不允许修改该文件。Claude 与 Responses 通道中，同一次运行里再次整篇读取内容未变的文件只返回 `unchanged: true`（不重复发送全文）；对话被压缩后重新返回全文；OpenAI 兼容服务与 Claude CLI 桥接不做此省略（它们会裁剪或自行压缩上下文）。 |
| `write_file(path, content, expected_sha256)` | 创建或替换文件；已有文件必须先读取并传回其版本，新文件使用 `new`。内容以 `1<Tab>` 等行号前缀开头（三行以上）时拒绝。 |
| `edit_file(path, old_text, new_text, expected_sha256?, replace_all?)` | 只替换唯一一次精确匹配；有多个匹配时拒绝（`replace_all: true` 时全部替换）。省略版本时使用本次运行最近读到的版本。精确匹配失败时依次尝试：去掉误带的行号前缀、逐行忽略行尾空白、弯引号视同直引号、整体缩进差一个统一前缀（同时调整新文本）；采用了容错时结果含 `match_notes`。仍失败时返回第一行出现的位置与该处当前内容（带行号，最多 15 行）；多处匹配时列出各处行号。文件在读取后被改动时不写入，返回改动差异并把当前版本登记为已读（差异过大时要求重新读取）。结果含 `updated_regions` 与增删行数；C#/VB 文件附 `compile_errors`。 |
| `multi_edit(path, edits, expected_sha256?)` | 对同一文件按顺序应用多处 `{old_text, new_text, replace_all?}`，任一失败则整体不写。 |
| `replace_symbol(name, new_text)` | 用 Roslyn 定位唯一一个声明（含文档注释与特性），整段替换为 `new_text`；`new_text` 为空时删除。重载用参数类型区分，如 `OrderService.Save(int)`；匹配多个时拒绝并列出候选。VS 中的声明与磁盘内容不一致时拒绝。审批、版本校验与备份同 `edit_file`。 |
| `replace_between(path, start_line, end_line, new_text, within?, expected_sha256?)` | 0.7.0。`within`（`类名.成员名`）把查找限定在该成员内（对应“两行在该成员内唯一”；VS 中的成员与磁盘不一致时拒绝）。从与 `start_line` 相同的整行（去掉首尾空白后比较，必须唯一）到其后第一处与 `end_line` 相同的整行（含两端）整段替换为 `new_text`，为空时删除。对应“替换片段”式改动（只给起止两行原文）。审批、版本校验与备份同 `edit_file`。 |
| `insert_near_symbol(name, text, position?)` | 在唯一匹配声明的前（`before`）或后（`after`，默认）整行插入代码。 |
| `check_file(path)` | 只读。用 Roslyn 按磁盘内容给出该文件的编译诊断（不编译解决方案），最多 100 条。文件不属于已加载项目时说明原因。 |
| `read_symbol(name)` | 只读。返回匹配声明的源码（含文档注释），最多 3 个、每个 400 行，并登记读取版本。 |
| `get_callers(name)` / `get_callees(name)` | 只读。调用层次：谁调用了该成员（附调用所在成员与代码行） / 该成员调用了哪些成员。 |
| `get_type_dependencies(name)` | 只读。类型依赖的其他类型（基类、接口、字段、参数、返回值、局部变量与创建对象），以及依赖它的类型。 |
| `delete_file(path, expected_sha256)` | 仅智能体模式。先读取并传回版本；逐次批准（审批面板显示将删除的全部内容）。批准后备份、记账，在禁止其他写入的句柄上再比较版本后删除。撤销时按备份以 CreateNew 重新创建。 |
| `move_file(path, new_path, expected_sha256)` | 仅智能体模式。目标不能已存在；逐次批准。先以 CreateNew 建目标（逐字节相同），再按删除流程删源；源删除失败时如实报告“两份都在”。两端各记一条变更，撤销分别恢复。不修改命名空间、项目文件与引用。 |
| `run_tests(project?, filter?, timeout_seconds?)` | 仅智能体模式。执行 `dotnet test <project> [--filter …] --logger trx`（受白名单约束，规则 `dotnet test`；逐次批准；默认 600 秒），结果写入工作区外的临时目录，读取 TRX 后删除。返回总数、通过、失败、跳过，最多 30 个失败用例的消息（2,000 字符）与堆栈（3,000 字符），以及输出末尾 6,000 字符。没有读到任何测试结果（编译失败、路径错误、筛选无匹配）时 `succeeded: false` 并注明，不报告成功。 |
| `run_command(command, args?, cwd?, timeout_seconds?)` | 仅智能体模式。见下文“命令执行”。 |
| `build_solution(project?)` | 仅智能体模式。每次先在审批面板请求批准（编译会执行 PreBuild / PostBuild 生成事件），然后编译整个解决方案（给出 `project` 时只编译该项目及其引用）并等待结束，返回是否成功及错误列表中最多 100 条错误/警告。 |
| `get_errors()` | 只读。返回错误列表中当前的错误与警告（编译与 IntelliSense），错误在前，最多 100 条。 |
| `update_plan(plan)` | 记录或更新任务计划（每行以 `done:` / `doing:` / `todo:` 开头，最多 60 步，同时只有一步进行中），只用于在气泡中展示进度，不改动工作区。 |
| `find_symbol(name)` | 只读。用 Roslyn 在已加载的 C#/VB 项目中按名称（可带前缀，如 `OrderService.Save`）查找类型与成员声明，最多 50 条。 |
| `find_references(name)` | 只读。查找匹配符号的全部引用（语义级，不是文本搜索），最多 100 处，附该行代码。 |
| `get_outline(path)` | 只读。列出 C#/VB 文件中声明的类型与成员及行号，不读全文。 |
| `get_hierarchy(name)` | 只读。对名称匹配的类型（最多前 10 个）：基类链（不含 object）、全部接口、派生类（传递）、接口的派生接口与实现类型；对方法 / 属性 / 事件：重写（overrides）与实现（implements）的上层成员、重写它（overridden-by）与实现它（implemented-by）的成员。框架符号 file 为空。最多 200 条。 |
| `get_project_graph()` | 只读。已加载解决方案中的 Roslyn 项目（多目标项目每个目标一项）：名称、语言、项目文件（工作区外只给文件名）、文档数、引用的项目。 |

写入已在编辑器中打开的文件时，经编辑器修改差异部分并静默保存：不弹“重新加载”，可在编辑器中 Ctrl+Z。前提是编辑器无未保存修改、内容与磁盘一致、保存编码与原文件一致；编码不一致或文件未打开时按原方式写磁盘。备份与意图记录先于修改落盘；编辑器在修改前拒绝时不留下改动记录；保存后实际磁盘内容与请求不同（例如保存时自动格式化）时在结果中提示。撤销同样优先经编辑器恢复。错误列表中工作区外的文件只返回文件名。

单文件上限默认 1 MiB（可在“设置”中修改），更大的文件只能分段读取。编码识别顺序：BOM（UTF-8 / UTF-16LE / UTF-16BE）→ 严格 UTF-8 → VS 进程的系统代码页（中文 Windows 为 GBK）。修改已有文件时保持原编码与 BOM，新建文件为无 BOM 的 UTF-8；内容含原编码无法表示的字符时拒绝写入。不转换原有换行，模型需要保留换行风格。不支持二进制或非白名单扩展名。

## 工作区来源

每次运行前通过 Visual Studio 项目查询 API 读取当前解决方案目录并填入工作区；未打开解决方案或查询失败时，使用手填或由附加选区推断的目录。

## Claude 5 系列（Opus 5.5）

- 发送 `thinking: {type: "adaptive"}` 与 `output_config.effort`（默认 `high`，可在设置中修改或留空）。
- 历史只追加：assistant 回合（含 thinking 块与签名）原样回传；system 提示与工具定义在整个对话内不变，读写模式写在每次运行的用户消息开头。
- 官方端点额外发送顶层 `cache_control` 与 `fallbacks: "default"`（beta `server-side-fallback-2026-07-01`，仅对 Opus 5.5 / Opus 5 / Fable 5.1 / Sonnet 5.5）。发生服务端回退时，最后一个 `fallback` 块之前的非文本块不回传。
- `stop_reason` 只接受 `end_turn` / `tool_use`；`refusal`、`max_tokens` 等一律报错且不执行工具。

## OpenAI Responses API（GPT-6 系列）

官方 OpenAI 端点（`api.openai.com`）上的 `gpt-6-astra`、`gpt-6.1-sol`、`gpt-6.1-terra`、`gpt-6-luna`、`gpt-6-sol`（含带日期的快照 ID）在 Chat Completions 中不能调用工具（OpenAI《Using GPT-6》），智能体请求改发 `POST {base}/responses`：

- 请求：`instructions`（system 提示，每次发送）、`input`（历史输入项）、扁平函数工具 `{type: "function", name, description, parameters, strict: false}`（参数含可选字段，不满足严格模式）、`stream: true`、`store: false`、`include: ["reasoning.encrypted_content"]`、`max_output_tokens`（GPT-6 上限 128K，默认 64,000）、选择了强度时 `reasoning.effort`。
- 历史：用户消息为 `{role: "user", content}`（带图片时为 `input_text` / `input_image` 部分）；模型每个回合的**全部输出项按顺序原样**写回（`reasoning` 含 `encrypted_content`、`message`、`function_call`，保留 id 与 phase），工具结果为 `{type: "function_call_output", call_id, output}`。
- 流：文本增量只用于显示；结果以 `response.completed` 中的 `output`（没有时用各 `response.output_item.done` 的项）为准。`status` 不是 `completed`、`response.incomplete`、`response.failed`、`error` 或流在完成事件前结束，一律报错且不执行任何工具。`message` 中的 `refusal` 视为拒答。
- 用量：`input_tokens` 含缓存命中，拆为未命中（`input_tokens - cached_tokens`）与缓存命中（`input_tokens_details.cached_tokens`）。
- 400 且历史含推理项（加密推理只能被同一模型系列使用）：去掉全部推理项后重试一次（`reasoning_stripped` 日志）。
- 不做发送时省略旧工具结果（会打断前缀缓存）；上下文超过窗口 80% 时用 Chat Completions 生成客户端摘要（GPT-6 用低强度）。
- 未在官方文档中逐字核实的部分：各流式事件的字段明细（`output_index`、`item`、`delta`）；实现只依赖完整输出项判定结果，事件字段缺失时不影响正确性。需用真实密钥验证。

## 工作区边界

拒绝绝对工具路径、`..`、NTFS ADS、设备名、尾点/空格、符号链接、junction 和硬链接。用户选定的根目录也不能位于链接路径、盘根或用户主目录。文件读写实现面向 Windows。

内置排除 `.git`、`.vs`、`bin`、`obj`、`node_modules` 等构建/依赖目录，以及 `.env*`、名称含 `secret` / `credential` 的路径和非源码扩展文件。这不是完整的凭据扫描：正常源文件仍可能包含密钥，发送前应自行审阅工作区。

启动时读取根目录 `.gitignore` 与 `.slcopilotignore` 的排除规则，支持 `*`、`**`、`?`、目录前缀和 `/` 根锚定；不处理 `!` 重新包含、不实现全部 Git ignore 语法，也不递归加载子目录的 `.gitignore`。规则在本次运行期间固定；修改规则后启动新运行。模型不能读取或修改 `.slcopilotignore`。

示例 `.slcopilotignore`：

```gitignore
private-data/
**/production-config/**
*.local.json
```

这是一层应用内访问约束，不是用于抵御恶意本地进程并发替换目录的操作系统沙箱。

## 写入、备份与撤销

默认只读；用户勾选当前运行的写入权限后，模型才可写入。模型无法提升权限。每次写入在备份与落盘之前，先把差异（审批预览上限 200,000 字符）交给工具窗口，等待用户 Approve / Reject；拒绝时不创建备份、不写盘，并以错误结果告知模型。批准后重新检查编辑器未保存状态与路径，随后在加锁的文件句柄上再次比较版本，审批期间的外部修改会被拒绝。写入前查询 VS 打开文档状态并拒绝未保存目标；随后持有禁止其他进程写入/删除的文件句柄，比较当前版本再修改文件。新建文件使用 CreateNew，避免无意覆盖已存在路径。

审批面板的“本次运行内始终允许此文件”（`WriteProposal.AllowForRun`）只在批准时生效：同一 `WorkspaceTools`（一次运行）中对同一完整路径的后续写入不再调用审批回调，记 `write_auto_approved` 日志并触发 `WriteAutoApproved` 事件；其余检查不变。删除与移动不读取该标记，始终逐次批准；命令、测试与编译走各自的审批。

首次改动某文件时，先保存原始字节与 manifest；再次修改保留第一次修改前的备份。写入意图先记入清单，成功后更新最终哈希。取消在单文件修改开始前生效，已开始的单文件写入尽量完成并记账。磁盘故障可产生不完整写入；保留的备份用于人工恢复，应用不会把它误报为成功。

删除的文件在清单中记为最终哈希 `absent`：撤销前确认该路径仍不存在（用户重新创建过则拒绝撤销），然后按备份重新创建。同一运行中删除后又写入的文件保留第一次的原始备份，撤销恢复到删除之前的内容。

差异预览是有大小上限的人类可读行差异，不是用于自动应用的 patch，也不显示所有换行/BOM 差异。完整内容以磁盘文件与备份为准。

撤销先检查所有文件的最终哈希和备份完整性，再逐个恢复；新建文件会被删除，但新建的空父目录保留。后续用户改动、损坏备份和不完整写入会阻止自动撤销。撤销不是跨文件原子事务；如果中途失败，只保留尚未恢复的记录。VS 重启或开始下一次运行后，旧运行需要通过保留的 manifest/原始备份人工恢复；没有隐藏的自动回滚。

## 命令执行（0.5.0）

`run_command` 与 `run_tests` 由 `CommandRunner` 执行：

- **白名单**：`profiles.json` 的 `AllowedCommands`（“设置 → 智能体命令白名单”），每行 `程序` 或 `程序 子命令`；后者要求第一个参数与子命令完全相同（区分大小写），因此 `git -c … status` 这类把全局选项放在子命令前的写法不匹配。默认：`dotnet build`、`dotnet test`、`dotnet restore`、`git status`、`git diff`、`git log`、`git show`、`git blame`。白名单在每次运行开始时读取，并写在该次运行的用户消息里告知模型。
- **不经过 shell**：程序名不能含路径，只接受 `.exe`（`.cmd` / `.bat` 由 cmd.exe 解释，会重新解析参数）；按 PATH 顺序查找（git 优先使用 VS 自带的 Git），忽略相对 PATH 项，位于工作区内的程序不执行。参数按 Windows 命令行规则逐个引用，最多 64 个，每个不超过 2,000 字符且不含换行。
- **审批**：每条命令在审批面板显示完整命令、程序路径、工作目录与超时，批准后才启动；拒绝时告知模型本次运行不要再请求。没有“始终允许”。
- **进程**：标准输入立即关闭（交互式提示读到文件结尾）；子进程不继承 `ANTHROPIC_API_KEY`、`OPENAI_API_KEY` 及各配置的密钥变量；设置 `MSBUILDDISABLENODEREUSE=1`（不留常驻 MSBuild 节点）、`DOTNET_NOLOGO=1`、`GIT_TERMINAL_PROMPT=0`。进程放入“关闭即结束”的 Windows 作业对象：超时或停止时结束整个进程树，并等待退出（最多 10 秒），仍未退出时如实返回 `still_running`；命令结束后它留下的子进程也会被结束。无法加入作业对象时立即结束该进程并报错，不在无法结束子进程的情况下运行。
- **输出**：stdout 与 stderr 分别保留开头 8 KB 与结尾 24 KB（在整行处截断），按 UTF-8 / 系统代码页解码，去掉 ANSI 颜色序列。
- **限制**：命令对文件的修改不经过审批差异、备份与撤销；命令可能执行项目代码（生成事件、测试代码）或仓库配置的程序（git 钩子、过滤器），批准前应确认。`Process.Start` 与加入作业对象之间有极短的窗口，期间创建的子进程不在作业中（实际中 dotnet / git 启动远慢于此）。

## MCP 工具（0.6.0）

执行路径：`ChatViewModel.PrepareMcpAsync → McpSession.StartAsync → McpServerConnection（StdioTransport / HttpTransport）`，工具经 `AgentProtocol.ExternalTools` 附加在 16 个内置工具之后，调用由 `AgentRunner` 交给 `McpSession.CallAsync`。

- **配置**：`McpConfigLoader` 读取用户级与工作区文件（≤ 64 KiB，拒绝符号链接），服务器名只含字母、数字、`_`、`-`（≤ 20），最多 10 个；`${env:NAME}` 在启动时替换，变量不存在则该服务器不启动。工作区配置按“工作区根目录 → 文件 SHA-256”记入 `%LOCALAPPDATA%\SLCopilot\mcp-trust.json`（临时文件 + 替换写入）。
- **协议**：JSON-RPC 2.0，协议版本 `2025-06-18`；initialize → notifications/initialized → tools/list（最多 10 页、每个服务器 100 个工具，全部服务器合计 100 个）→ tools/call。stdio 为一行一条 UTF-8 JSON（单条 ≤ 8 MB），stdout 上的非 JSON 行忽略；服务器的 ping 请求回复空结果，其他请求回复 -32601。HTTP 每条消息 POST，接受 JSON 或事件流，带 `Mcp-Session-Id` 与 `MCP-Protocol-Version`。超时或取消时发送 `notifications/cancelled`。
- **工具名与说明**：`mcp__服务器__工具`，非法字符替换为 `_`，超过 64 字符时截短并加 6 位哈希；说明前加“外部 MCP 服务器、不可信、每次调用需批准”的前缀，截断到 1,000 字符；`inputSchema` 原样声明（去掉 `$schema`，不是 object 时替换为空 object）。
- **审批与模式**：每次调用经审批面板（服务器命令或地址、工具、参数），“本次运行内始终允许此工具”只在内存中、下一次运行清除；只读运行只能调用 `annotations.readOnlyHint` 为 true 的工具（服务器自述，不是安全保证，仍需批准）。拒绝、超时、服务器错误与断开都作为错误结果返回给模型，不终止运行。
- **生命周期**：会话属于一个智能体对话；新对话、打开历史对话时释放（结束 stdio 进程树），撤销检查点后沿用。服务器中途退出后其工具调用返回错误，需新对话重启。
- **未知**：Claude / OpenAI 接口对“历史中出现、但当前请求未声明的工具”的处理未验证；因此已有内容的对话不中途加入或移除 MCP 工具。

## 本版不包含

通用 shell、MCP 的 resources / prompts / sampling / OAuth（Claude CLI 经桥接调用本扩展的工具见 [CLAUDE_CLI_BRIDGE.md](CLAUDE_CLI_BRIDGE.md)，反向由本扩展驱动 Claude CLI 不支持）、VS 测试资源管理器集成（非 SDK 风格测试项目不能用 `run_tests`）。

审批时可在 VS 差异窗口中打开改动：左侧为当前文件（只读），右侧为模型建议（可编辑）。批准时写入右侧内容（含未保存的编辑），并告知模型“用户修改过”（`user_edited`）；右侧内容读取失败时不批准。用户版本同样经过 NUL、大小与编码检查。

项目指令（`.slcopilot/instructions.md` 或 `.github/copilot-instructions.md`，最多 16,000 字符）在内容变化时附加到下一条用户消息，system 保持不变；受忽略规则约束。
