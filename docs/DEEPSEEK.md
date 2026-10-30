# DeepSeek 配置

设置中先“新增配置”，命名为 DeepSeek，再在服务商下拉框选择 **DeepSeek**，保存 API Key 和配置。
默认地址为 `https://api.deepseek.com/v1`，也可设置提供 DeepSeek 协议的网关地址。
环境变量方式使用 `DEEPSEEK_API_KEY`，设置后重启 Visual Studio。

模型菜单预置 `deepseek-flash`、`deepseek-v4-pro`。点击获取模型可读取账户当前可用列表，也可手动填写模型 ID；网关若仍提供 `deepseek-chat`、`deepseek-reasoner`，可以手动填写，并先使用默认推理强度。模型是否可调用由服务商决定。

推理强度支持默认、关闭、低、高、最高。默认不额外发送思考参数。行内补全关闭思考以减少延迟。聊天和智能体使用 `max_tokens`，智能体保留流式 `reasoning_content` 并在后续工具调用中回传。现有 OpenAI 配置若指向 `api.deepseek.com`，自动启用兼容处理。旧 medium 强度映射为 high。

输出仍遵守插件的 32,768 token 上限。模型列表读取上下文与输出元数据；不同模型的图片能力和账户权限以服务商为准。

协议依据：[思考模式及工具回传](https://api-docs.deepseek.com/guides/thinking_mode/)、[模型列表](https://api-docs.deepseek.com/api/list-models/)。自动化验证使用模拟 HTTP/SSE，不代表真实账户 API 或 Visual Studio 界面已通过端到端测试。
