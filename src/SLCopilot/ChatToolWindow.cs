using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace SLCopilot;

/// <summary>SLCopilot 聊天工具窗口；内容为原生 WPF 控件，视图模型由 Package 在异步初始化时提供。</summary>
[Guid("3f2b8c1a-5d4e-4f6a-9b7c-1e2d3c4b5a62")]
public sealed class ChatToolWindow : ToolWindowPane
{
    public ChatToolWindow(ChatViewModel viewModel) : base(null)
    {
        Caption = "SLCopilot";
        Content = new ChatControl(viewModel);
    }
}
