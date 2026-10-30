using System.Text.RegularExpressions;

namespace SLCopilot.Core;

/// <summary>运行模式：询问（只读回答）、智能体（可写入，逐次批准）、计划（只读调查后给出实施计划，批准后再执行）。</summary>
public enum RunMode { Ask, Agent, Plan }

/// <summary>
/// 输入区的斜杠命令。<see cref="Mode"/> 为 null 时沿用当前模式。<see cref="Source"/> 为自定义命令的来源文件（相对工作区），内置命令为 null。
/// 模板中的 <c>$ARGUMENTS</c> 替换为命令后的文字；没有该占位符时文字附加在模板之后。
/// </summary>
public sealed record PromptCommand(string Name, string Description, string Template, RunMode? Mode, string? Source = null);

/// <summary>解析结果。<see cref="Text"/> 为展开命令、替换 #file 标记后的提示；<see cref="Files"/> 为引用的相对路径（去重，保持顺序）。</summary>
public sealed record ParsedPrompt(PromptCommand? Command, string Text, IReadOnlyList<string> Files);

/// <summary>解析 /命令 与 #file:路径 引用。纯文本处理，不访问文件；路径校验由调用方按工作区策略完成。</summary>
public static class PromptCommands
{
    public static readonly IReadOnlyList<PromptCommand> All =
    [
        new("explain", "解释附加代码或指定文件的行为、问题和改进建议（询问）",
            "请解释相关代码的行为、潜在问题和改进建议。", RunMode.Ask),
        new("fix", "找出并修复问题；先读错误列表，改完编译验证（智能体）",
            "请找出并修复问题：先用 get_errors 读取当前错误（如有），阅读相关代码后修改，最后用 build_solution 验证。", RunMode.Agent),
        new("tests", "为相关代码编写单元测试并运行，沿用项目现有测试框架（智能体）",
            "请为相关代码编写单元测试：沿用项目现有的测试框架、目录和命名约定，覆盖正常与异常路径；写完后用 run_tests 运行相关测试并修复失败。", RunMode.Agent),
        new("doc", "为相关代码补充 XML 文档注释，不改变行为（智能体）",
            "请为相关代码补充 XML 文档注释（中文 summary，写职责契约、失败行为与线程约束），不要改变任何行为。", RunMode.Agent),
        new("debug", "分析调试中断时的异常：结合调用栈、局部变量与源码找根因（询问）",
            "请分析下面的调试中断信息：先结合调用栈与局部变量阅读相关源码，找出根因（区分已从代码确认的和推断的），再给出修复建议和验证方法；不要修改文件。", RunMode.Ask),
        new("plan", "只读调查后给出实施计划，批准后再执行（计划）", "", RunMode.Plan),
        new("review", "审查暂存区的改动（没有时审查智能体最近的改动），只给意见不修改（询问）", "", RunMode.Ask),
        new("commit", "根据暂存区的 Git 改动生成提交说明（不会执行提交）", "", null),
        new("diag", "自检：配置、密钥、服务连通、工作区、Roslyn、git、行内补全", "", null),
        new("init", "分析解决方案，生成或更新项目指令文件，之后每次运行自动带上（智能体）",
            "请分析当前解决方案，生成或更新项目指令文件：已有 .slcopilot/instructions.md 时更新它；没有它但有 .github/copilot-instructions.md 时更新后者；都没有时新建 .slcopilot/instructions.md。" +
            "更新时先读取原文件，保留人工写的内容。先用 get_project_graph、list_directory、get_outline、read_symbol 和 read_file 了解：各项目的职责与依赖方向、技术栈与框架版本、" +
            "编译与测试方式、代码约定（命名、注释语言与密度、异步与异常处理、日志、MVVM 或依赖注入等模式），以及修改代码时不能违反的约束。" +
            "文件用简洁的条目写成（不超过 150 行），只写从代码中能确认的事实并尽量注明代表性文件，不写泛泛的建议。该文件会在之后每次运行时随请求发送给模型。", RunMode.Agent),
        new("remember", "把一条要求记入项目指令文件，之后每次运行都会带上（如：/remember 日志用 NLog，不要用 Console）", "", null),
        new("compact", "把当前对话压缩为摘要，释放上下文（长任务中途可用）", "", null),
        new("clear", "开始新对话", "", null),
        new("help", "显示命令、引用方式与快捷键", "", null),
        new("export", "把当前对话导出为 Markdown 文件（含工具记录与差异）", "", null),
    ];

    // #file:路径，或 Claude Code 风格的 @路径：@ 前为空白或开头，且以常见源码 / 配置文件扩展名结尾，
    // 避免把邮箱、@提及与 @Model.Name、@app.route 之类的代码当成文件引用。
    private static readonly Regex FileReference = new(
        @"#file:(?:""(?<path>[^""]+)""|(?<path>[^\s,;，；]+))|(?<![^\s(（])@(?<path>[^\s,;，；@]*[^\s,;，；@.]\.(?:" + CodeReference.Extensions + @"))(?![\w.])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>命令名：字母开头，字母、数字、下划线或连字符。</summary>
    public const string NamePattern = "[a-zA-Z][a-zA-Z0-9_-]*";

    /// <summary>按名称查找命令：内置命令优先，其次是 <paramref name="custom"/> 中的自定义命令。不区分大小写。</summary>
    public static PromptCommand? Find(string name, IReadOnlyList<PromptCommand>? custom = null) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) ??
        custom?.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static ParsedPrompt Parse(string input, IReadOnlyList<PromptCommand>? custom = null)
    {
        var text = (input ?? "").Trim();
        PromptCommand? command = null;
        var match = Regex.Match(text, "^/(?<name>" + NamePattern + @")(?=\s|$)");
        if (match.Success && Find(match.Groups["name"].Value, custom) is { } found)
        {
            command = found;
            var rest = text.Substring(match.Length).Trim();
            text = found.Template.IndexOf("$ARGUMENTS", StringComparison.Ordinal) >= 0 ? found.Template.Replace("$ARGUMENTS", rest).Trim()
                : found.Template.Length == 0 ? rest : rest.Length == 0 ? found.Template : found.Template + "\n\n" + rest;
        }
        var files = new List<string>();
        text = FileReference.Replace(text, m =>
        {
            var path = m.Groups["path"].Value.Trim().Replace('\\', '/');
            if (!files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
            return "`" + path + "`";
        });
        return new(command, text, files);
    }

    /// <summary>附加到消息末尾的引用说明，提示模型用 read_file 读取。</summary>
    public static string ReferenceNote(IReadOnlyList<string> files) =>
        files.Count == 0 ? "" : "\n\n[Referenced files: " + string.Join(", ", files) + ". Read them with read_file before answering.]";
}
