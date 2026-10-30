# 参考项目与技术选择

调研日期：2026-10-29。以下项目用于研究，不代表已经引入其代码；采用或移植代码之前需要单独核对对应版本许可证。

| 项目 | 用途与参考价值 |
| --- | --- |
| [VisualChatGPTStudio](https://github.com/jeffdapaz/VisualChatGPTStudio) | 最接近目标的 Visual Studio 扩展；有聊天、编辑器命令、代码建议、选区优化等功能，可研究交互方式与解决方案上下文。 |
| [ClaudeCodeExtension](https://github.com/dliedke/ClaudeCodeExtension) | Visual Studio 中集成 Claude Code 的路线；可用于比较 CLI 集成与直接 API 接入的区别。 |
| [Microsoft VSExtensibility](https://github.com/microsoft/VSExtensibility) | 官方扩展 API 与示例，特别是 ToolWindowSample；是本项目扩展宿主、Remote UI 和命令模型的主要技术依据。 |

## 已选择的实现

0.3.0 起改为传统 VSSDK 进程内扩展（AsyncPackage + WPF 工具窗口，net472）。原因：VisualStudio.Extensibility 进程外扩展由 Visual Studio Installer 安装、需要联网，且 Remote UI 只允许纯 XAML，无法实现聊天自动滚动等交互。核心层同时编译 net472 与 net8.0，不依赖 IDE，可以独立测试两个服务商的协议、流式事件、取消和错误处理。模型调用与 Agent 在线程池执行，界面更新经有序队列回到 UI 线程。

Claude 使用 `x-api-key`、`anthropic-version: 2023-06-01` 和 `/v1/messages`；OpenAI 兼容模式使用 Bearer 与 `/v1/chat/completions`。不自动重试生成请求，以免造成重复计费。

只在用户明确发送时上传选区。代码修改由智能体完成（写前审批、版本校验、备份与撤销）；0.5.1 起移除了“生成 → 预览 → 应用到选区”的旧流程。

官方资料：

- [Visual Studio 工具窗口示例](https://github.com/microsoft/VSExtensibility/tree/main/New_Extensibility_Model/Samples/ToolWindowSample)
- [Visual Studio 编辑器命令](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/get-started/tutorial-create-simple-extension)
- [Claude 流式协议](https://platform.claude.com/docs/en/build-with-claude/streaming)
- [OpenAI Chat Completions 流式事件](https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events)

## 对标 Copilot 的后续阶段

1. 先完成 IDE 实机验收和真实服务联调，补充中文本地化、Markdown、凭据管理器、模型列表与成本统计。
2. 编辑体验：真正的 diff 视图、快捷键、右键操作、自动行内建议与 Tab 接受；需要先验证编辑器 API 能力和性能。
3. 项目上下文：用户明确选择文件、忽略规则、token 预算、检索与引用来源。
4. Agent：0.2.0 已实现工作区多文件读写、工具循环、备份与差异预览；下一阶段是计划界面、受控构建/测试执行、MCP 和更完善的 IDE 文件事务集成。

除明确标注 0.2.0 已实现的项目外，这些仍是后续路线。

### 2026-10-29 对比更新（0.5.0）

与 Copilot（VS）和 Claude Code 对比，最大的差距是“验证闭环”：智能体能改、能编译，但不能运行测试或命令。0.5.0 补齐：`run_tests`（dotnet test + TRX 解析）、`run_command`（白名单 + 每次批准，不经过 shell，作业对象结束进程树）、`delete_file` / `move_file`（备份与撤销）、`find_files` 与正则 / 通配搜索。

选择“白名单 + 每次批准”而不是 Claude Code 的通用 shell：shell 会展开管道、重定向与变量，审批时看到的命令与实际执行的不一致，与“写前审批、可撤销”的定位冲突。

后续路线（按优先级）：

1. 0.5.1 批准分级（已实现）：审批面板加“本次运行内始终允许此文件”（仅内存，不落盘）；删除、移动、命令与编译不提供。同版加入仿 Copilot 的“已修改文件”卡片（+/− 行数，点击打开差异窗口）。
2. 0.6.0 MCP 客户端（已实现）：stdio + Streamable HTTP，手写 JSON-RPC（不引入 SDK，避免 VS 进程内的依赖与 net472 兼容风险）；每次调用需批准；工作区配置（`.slcopilot/mcp.json` 或 `.mcp.json`）按内容哈希确认信任；工具列表在对话内固定。
3. 0.6.1 图片附件（已实现）；0.6.x 分层指令（子目录指令文件、`applyTo`）、检查点跨 VS 重启恢复。
4. 暂缓：Next Edit Suggestions / 任意位置补全、subagent、hooks、语义索引。

0.2.0 协议依据：[Claude 工具结果处理](https://platform.claude.com/docs/en/agents-and-tools/tool-use/handle-tool-calls)、[OpenAI function calling](https://developers.openai.com/api/docs/guides/function-calling)。
