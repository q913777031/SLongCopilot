# SLongCopilot

**在 Visual Studio 中使用自己的 AI 模型，完成代码问答、项目修改和开发任务。**

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
![Version](https://img.shields.io/badge/version-0.8.0--preview-blue)
![Visual Studio](https://img.shields.io/badge/Visual%20Studio-2022%2017.14%2B-purple)

SLongCopilot（Visual Studio 扩展名为 **SLCopilot**）是面向 **Visual Studio 2022** 的开源 AI 编程助手。它直接集成到 IDE 中，连接你配置的 Claude、OpenAI、DeepSeek 或兼容 API，结合编辑器、解决方案和 Roslyn 语义信息，帮助你理解代码、规划任务、修改文件、分析异常和运行验证。

项目目前为 **0.8.0 预览版**，采用 [MIT 许可证](LICENSE)。使用模型服务需要你自己的 API 密钥，调用费用由对应服务商收取。

**下载：** [Windows 安装包（VSIX）](https://github.com/q913777031/SLongCopilot/releases/download/v0.8.0/SLongCopilot-0.8.0.vsix) · [发布说明与校验文件](https://github.com/q913777031/SLongCopilot/releases/tag/v0.8.0)

## 核心功能

| 功能 | 说明 |
| --- | --- |
| 多模型接入 | 支持 Claude、OpenAI、DeepSeek 和兼容服务；可保存多套配置、读取模型列表、切换模型与推理强度。 |
| 项目代码问答 | 结合当前文件、光标、选区和文件引用回答问题；支持流式输出、Markdown、图片输入及对话历史。 |
| 工作区智能体 | 浏览、搜索、读取和修改项目文件，支持精确替换、符号级编辑、多文件任务与任务计划。 |
| Visual Studio 集成 | 使用 Roslyn 查找定义、引用、调用关系和类型层次；读取错误列表，编译解决方案，分析调试异常。 |
| 改动审阅与恢复 | 展示差异，校验文件版本，备份写入前内容；支持审批、撤销与当前会话内的检查点恢复。 |
| 行内补全 | 可选的代码补全，支持 Tab 接受；默认关闭，可单独配置补全模型。 |
| MCP 扩展 | 连接 stdio 或 Streamable HTTP MCP 工具服务；支持从环境变量读取服务凭据。 |
| Claude CLI 桥接 | 在本机提供 MCP 工具，让 Claude CLI 通过 Visual Studio 进程读取和修改工作区。 |

## 使用模式

| 模式 | 适合的任务 |
| --- | --- |
| 询问 | 理解代码、查找实现、分析问题；打开解决方案时可以只读浏览项目。 |
| 智能体 | 修改代码、修复错误、重构、生成测试及执行允许的开发命令。 |
| 计划 | 先只读调查并生成实施计划，再由你选择执行。 |
| Claude CLI | 将 IDE 内的工作区工具提供给 Claude CLI；与内置模式切换使用。 |

你可以直接输入自然语言，例如：

```text
解释当前选中的代码，并找出它的调用位置。

检查这个解决方案的项目依赖，给出重构计划。

修复当前文件的编译错误，列出每处改动的原因。
```

常用命令包括 `/explain`、`/fix`、`/tests`、`/plan`、`/review`、`/debug`、`/commit`、`/diag`、`/compact` 和 `/export`。输入 `/help` 查看完整说明；使用 `@路径` 或 `#file:路径` 引用项目文件。`/commit` 仅生成提交说明，不执行 Git 提交。

## 安装

### 环境要求

- Windows，以及 **Visual Studio 2022 17.14.x**。当前安装清单支持范围为 `[17.14, 18.0)`，目标架构为 x64 / ARM64；Visual Studio 2026 不在当前清单范围内。
- 可访问所配置模型服务的网络环境，以及对应服务的 API 密钥。
- 如需自行编译，安装 .NET SDK 8.0 和 Visual Studio 的“Visual Studio 扩展开发”工作负载。

### 安装扩展

1. 从 [Releases](https://github.com/q913777031/SLongCopilot/releases/tag/v0.8.0) 下载 `SLongCopilot-0.8.0.vsix`。也可以从成功的 **Actions → Build and test** 构建中下载 `SLCopilot-vsix` artifact，或按下方开发步骤自行编译。
2. 关闭 Visual Studio，双击下载的 `.vsix` 文件，按 VSIXInstaller 提示安装；如果下载的是 Actions artifact，请先解压。
3. 重新打开 Visual Studio 和你的解决方案，从 **工具** 菜单打开 SLCopilot 聊天窗口。

如果正在从旧的 0.2.x 扩展升级，请先在扩展管理器中卸载旧版；新旧扩展标识不同，可能出现重复菜单。

## 配置模型

1. 打开聊天窗口右上角的 **设置**，新增或选择一套模型配置。
2. 选择服务商，填写 API 根地址。
3. 在 **API 密钥** 输入框中填写密钥并点击 **保存密钥**；也可以配置下表中的环境变量，之后重启 Visual Studio。
4. 点击 **获取列表** 选择可用模型，或手动填写模型 ID，然后保存配置。
5. 选择询问、智能体或计划模式，开始使用。

| 服务 | API 根地址示例 | 环境变量 |
| --- | --- | --- |
| Claude | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` |
| OpenAI | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| DeepSeek | `https://api.deepseek.com/v1` | `DEEPSEEK_API_KEY` |
| 兼容服务 | 服务商提供的 HTTPS API 根地址 | 自定义环境变量名 |

密钥保存在 **Windows 凭据管理器** 中，按模型配置区分；配置文件仅保存服务参数和环境变量名称。不要把密钥本身填写到“环境变量名”一栏。模型和能力是否可用取决于服务商与账户权限。

OpenAI 接入包含 Chat Completions，以及代码中指定模型的 Responses API 路由；兼容服务需要支持所选模式使用的流式协议和工具调用。DeepSeek 的详细说明见 [配置指南](docs/DEEPSEEK.md)。

## 权限与数据

- **当前版本默认开启自动批准。** 如果希望每次修改或执行前先审阅，请在设置中关闭自动批准；文件版本校验、写入备份和写入保护仍会生效。
- 智能体可以使用工作区文件、搜索结果和工具输出完成任务；这些内容会随请求发送到你选择的模型服务。行内补全开启后也会发送光标附近代码。
- 对话、运行日志和写入备份存放在 `%LOCALAPPDATA%\SLCopilot`。对话与备份可能包含源码，分享诊断材料前请自行检查。
- 可使用 `.slcopilotignore` 排除不应被工作区工具读取的文件，使用 `.slcopilot/settings.json` 中的 `protect` 规则限制写入。命令白名单和 MCP 工具权限需另行配置。
- 项目指令支持 `.slcopilot/instructions.md`、按路径生效的指令及自定义斜杠命令。详细边界见 [智能体架构与权限说明](docs/AGENT.md)。

## MCP 与 Claude CLI

MCP 服务可配置在用户级 `%LOCALAPPDATA%\SLCopilot\mcp.json`，或工作区 `.slcopilot/mcp.json` / `.mcp.json` 中。工作区配置首次使用或内容变化时需要确认信任；服务密钥请使用 `${env:VARIABLE_NAME}` 占位符引用环境变量。

Claude CLI 桥接仅监听本机回环地址，并使用令牌认证。文件由 Visual Studio 进程读写，适用于需要由 IDE 进程访问源码的场景。注册方法、权限和限制见 [Claude CLI 桥接指南](docs/CLAUDE_CLI_BRIDGE.md)。

## 本地开发

在安装了开发依赖的 **Developer PowerShell for Visual Studio 2022** 中执行：

```powershell
# 编译并生成 VSIX，不自动部署到实验实例
msbuild SLCopilot.sln /restore /p:Configuration=Release /p:TreatWarningsAsErrors=true /p:DeployExtension=false /m

# 检查安装包中的程序集、菜单注册、许可证与调试符号
pwsh -File scripts/Test-Vsix.ps1 -Path src/SLCopilot/bin/Release/net472/SLCopilot.vsix

# 运行核心测试；Windows 上覆盖 net8.0 与 net472
dotnet test tests/SLCopilot.Core.Tests/SLCopilot.Core.Tests.csproj -c Release
```

生成的安装包位于 `src/SLCopilot/bin/Release/net472/SLCopilot.vsix`。VSIX 打包需要完整 MSBuild；调试扩展时可在 Visual Studio 中按 F5 启动实验实例。

```text
src/SLCopilot.Core          模型协议、智能体、工作区工具、MCP、配置与会话
src/SLCopilot               Visual Studio 扩展、WPF 界面、编辑器与 Roslyn 集成
tests/SLCopilot.Core.Tests  核心功能、协议和边界测试
scripts                    VSIX 安装包检查
docs                       架构、配置、研究与验收说明
```

Release 构建会映射本机源码路径，安装包排除 PDB 并附带 MIT 许可证。不要将本机 `.env`、私钥、MCP 令牌、对话、备份和诊断产物提交到仓库。

## 文档与参与贡献

- [智能体架构、工作区工具与权限](docs/AGENT.md)
- [DeepSeek 配置](docs/DEEPSEEK.md)
- [Claude CLI 桥接](docs/CLAUDE_CLI_BRIDGE.md)
- [验收清单](docs/VALIDATION.md)
- [参考项目与路线图](docs/RESEARCH.md)

欢迎通过 Issue 报告问题或提出建议，也欢迎提交 Pull Request。报告问题时请提供扩展版本、Visual Studio 版本、复现步骤与脱敏后的错误信息。

项目处于预览阶段。自动化测试主要使用模拟 HTTP/SSE 和临时工作区；构建与包检查不代表真实模型服务、所有 IDE 操作或全部架构已完成端到端验收。

## 许可证

[MIT](LICENSE) · Copyright (c) 2026 SLCopilot contributors。依赖组件保留各自的许可证。
