# Claude CLI 桥接（0.8.0）

让 Claude CLI（Claude Code 命令行）通过 SLCopilot 读改代码：Claude CLI 负责推理与决策，**所有文件读写、搜索、Roslyn 导航与编译都在 Visual Studio 进程（devenv.exe）内完成**。适用于进程透明加密的源码：devenv.exe 读到明文、写回时由加密软件重新加密；claude.exe 本身不接触磁盘上的源码。

```
claude.exe ──MCP / HTTP（127.0.0.1 + Bearer 令牌）──▶ SLCopilot（devenv.exe 内）──▶ WorkspaceTools / Roslyn / 编辑器 ──▶ 磁盘
```

## 使用

1. VS 中打开解决方案，打开 SLCopilot 聊天窗口（扩展随之加载）。
2. 在输入区左下角的模式下拉框中选择 **Claude CLI**。桥接启动后，输入区上方显示 Claude CLI 模式说明；“设置”中的状态行显示“运行中：127.0.0.1:47821（读写）…”。需要 Claude 修改代码时，在“设置”中保留 **允许 Claude CLI 写入、编译、测试与执行白名单命令**。
3. 在“设置”中点 **复制注册命令**，在命令行执行一次（用户级注册，所有目录可用）：

   ```bash
   claude mcp add --transport http --scope user slcopilot http://127.0.0.1:47821/mcp --header "Authorization: Bearer <令牌>"
   ```

4. 点 **复制启动命令**，在解决方案目录中启动 Claude CLI：

   ```bash
   claude --disallowedTools Read Write Edit MultiEdit NotebookEdit Grep Glob Bash
   ```

   在 Claude CLI 中执行 `/mcp` 应看到 `slcopilot` 已连接，工具名为 `mcp__slcopilot__read_file` 等。

**必须禁用内置文件工具与 Bash**：它们由 claude.exe 直接读写磁盘，读到的是密文；对密文执行 Edit / Write 会写坏文件或写出明文文件，Bash 也能绕过整套机制。桥接在初始化说明（instructions）中也要求模型只用桥接工具，但这只是提示，禁用才是保证。同理，解决方案目录中的 `CLAUDE.md` 对 claude.exe 也是密文；项目规则请放在 `.slcopilot/instructions/*.md`（`applyTo`），由桥接随工具结果交给模型。

## 与内置智能体二选一

Claude CLI 模式与 SLCopilot 自己的模式（询问 / 智能体 / 计划）**不能同时使用，只能切换**，任何时刻只有一方在读写工作区：

| 动作 | 条件与行为 |
| --- | --- |
| 切到 Claude CLI | 本窗口有任务在运行（含等待审批）时下拉框不可用；切换时启动桥接，启动失败（端口被占用、凭据不可用）则保持原模式并说明原因 |
| Claude CLI 模式中 | 本窗口的智能体、询问、计划、普通聊天、`/remember`、`/compact`、撤销与检查点恢复都拒绝开始，状态栏提示；`/diag`、导出、查看差异可用。“按此计划执行”“继续”被清除 |
| 切回其他模式 | 先停止监听并取消 Claude CLI 正在执行的调用，**等它们全部结束**（正在写入的文件写完、编译取消完成、命令进程树结束）才切换；等待期间状态栏显示剩余调用数，本窗口仍不能开始任务。不设超时：调用未结束时无法确认工作区状态。被取消的调用向 Claude CLI 返回 `isError` |
| 关闭再打开 VS | 恢复上次的模式；上次是 Claude CLI 模式但桥接启动失败时回到智能体模式并说明 |
| Claude CLI 模式中改端口、读写权限或重新生成令牌 | 同样先等旧桥接的调用结束再启动新桥接，两个桥接的写入不会重叠 |

切换在 UI 线程上串行执行，与本窗口任务的开始检查（`TryBegin`）在同一线程，检查与开始之间不会切换模式。日志：`work_mode`（mode=cli / agent）、`bridge_draining`（等待的调用数）。

## 工具

与内置智能体相同（见 [AGENT.md](AGENT.md)），不含只用于界面的 `update_plan`。只读模式只提供只读工具（`read_file`、`read_symbol`、`find_references`、`get_callers`、`get_errors` 等）。

- 路径相对解决方案目录；Claude CLI 传入的、位于解决方案内的绝对路径自动转为相对路径，解决方案外的路径被拒绝。
- 每次工具调用前读取 VS 当前打开的解决方案；未打开时返回错误。切换解决方案后，该会话改用新目录（读取版本重新登记）。
- 每个 Claude CLI 会话一个 `WorkspaceTools`：读取版本、备份目录与运行编号按会话隔离；备份在 `%LOCALAPPDATA%\SLCopilot\backups\<运行编号>`。
- 写入、删除、移动、编译、测试与命令全局串行；只读工具最多 4 个并行。
- 审批：跟随“自动批准”设置。关闭自动批准时，审批出现在 SLCopilot 窗口（自动显示），标题以“Claude CLI：”开头；与智能体的审批排队，一次只显示一项。
- 写入保护（`.slcopilot/settings.json` 的 `protect`）、未保存编辑器文件不可写、版本校验、编码保持、编辑器内修改（可 Ctrl+Z）均与智能体相同。

## 失败与取消

| 情况 | 结果 |
| --- | --- |
| 令牌错误 / 缺失 | HTTP 401；每分钟最多一条 `bridge_unauthorized` Warn（附累计次数） |
| Host 不是 `127.0.0.1:端口` 或 `localhost:端口`、请求带 `Origin` | HTTP 403（防 DNS 重绑定与浏览器跨站请求） |
| 工具执行失败、文件被改动、写入保护、未打开解决方案 | `isError: true` 与原因，**从不报告为成功** |
| Claude CLI 发送 `notifications/cancelled`（Esc 中断） | 该调用被取消，返回 `isError: true`；已开始的单文件写入尽量完成，模型被要求重读文件 |
| 切回内置智能体 | 停止监听，进行中的调用被取消并等待其结束（等待中的审批随之结束） |
| 关闭 VS | 停止监听，进行中的调用被取消，不等待 |
| 单次调用超过 2 小时（含等待审批） | 取消并返回错误 |
| 端口被占用（如另一个 VS 实例已开启桥接） | 桥接不启动，状态行说明；换端口后按新端口重新注册 |

## 安全

- 只监听 `127.0.0.1`，Windows 上独占端口；不支持 GET（SSE）与 JSON-RPC 批量，请求体最大 8 MB，同时最多 16 个连接、16 个会话。
- 令牌为 256 位随机数，保存在 Windows 凭据管理器（`SLCopilot:bridge-token`），常数时间比较；**注册命令中含令牌**，`claude mcp add` 会把它写入 `%USERPROFILE%\.claude.json`。“重新生成令牌”使旧令牌立即失效，之后执行 `claude mcp remove slcopilot -s user` 并用新命令重新注册。
- **桥接是一个解密通道**：持有令牌的本机进程都能经它读到明文源码（与 SLCopilot 自身把代码发送给模型服务相同）。启用前确认符合本单位的加密与数据外发规定；不用时关闭。
- 运行日志（`%LOCALAPPDATA%\SLCopilot\logs`）记录 `bridge_started` / `bridge_stopped` / `bridge_session_opened` / `bridge_workspace`（会话、根目录、备份目录）/ `bridge_call`（工具、耗时、是否失败）/ `bridge_call_canceled`，不记录代码与令牌；`bridge_workspace` 与 `bridge_call` 的 run 字段即备份目录名。

## 限制

- 只在 SLCopilot 已加载时可用（打开过聊天窗口，或 VS 启动时恢复了该窗口）；否则 Claude CLI 显示 slcopilot 连接失败，打开聊天窗口后在 Claude CLI 中执行 `/mcp` 重连。
- 切回内置智能体后，Claude CLI 中的 slcopilot 工具连接失败；再切回 Claude CLI 模式后在 Claude CLI 中执行 `/mcp` 重连。
- Claude CLI 的改动不进入本窗口的“已修改文件”卡片与检查点，只能从备份目录恢复；内置智能体在之后读文件时会看到这些改动（文件版本已变化）。
- 修改端口或读写模式会重启桥接，已连接的 Claude CLI 需执行 `/mcp` 重新连接。
- `run_command` 仍只执行命令白名单内的程序，不经过 shell。
- Claude CLI 侧的 MCP 工具超时（`MCP_TOOL_TIMEOUT`）短于编译或审批耗时时，Claude CLI 会先放弃等待，但 VS 中的操作照常完成；之后让模型用 `get_errors` 或重读文件核实结果。
- 未在 Windows 实机与真实 Claude CLI 上验证；协议层由 `McpBridgeTests` 覆盖（HTTP 鉴权、会话、工具列表、错误与取消）。
