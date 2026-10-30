namespace SLCopilot.Core;

/// <summary>编译结果。<see cref="Canceled"/> 为 true 表示编译被取消（用户或 VS），结果不可作为成功依据。</summary>
public sealed record BuildOutcome(bool Succeeded, bool Canceled);

/// <summary>错误列表中的一项。<see cref="File"/> 为完整路径或空。</summary>
public sealed record IdeDiagnostic(string Severity, string File, int Line, int Column, string Project, string Message);

/// <summary>代码符号或引用位置。<see cref="File"/> 为完整路径；<see cref="Line"/> 从 1 开始；<see cref="Text"/> 为签名或该行代码。<see cref="Depth"/> 用于大纲缩进。</summary>
public sealed record CodeSymbol(string Kind, string Name, string File, int Line, string Text, int Depth = 0);

/// <summary>解决方案中的一个项目：名称、语言、项目文件完整路径（可能为空）、文档数与引用的其他项目名称。</summary>
public sealed record ProjectNode(string Name, string Language, string File, int Documents, IReadOnlyList<string> References,
    string AssemblyName = "", string OutputKind = "");

/// <summary>一处声明的源代码。<see cref="File"/> 为完整路径；行号从 1 开始；<see cref="Text"/> 含声明前的文档注释，过长时已截断。</summary>
/// <param name="WholeLines">
/// 声明是否独占所在的行（首行声明前只有空白，末行声明后只有空白或注释，且不是多变量字段声明的一部分）。
/// 为 false 时（如 <c>int a, b;</c>、单行枚举的成员、位置记录的参数）按整行替换会改动相邻代码，replace_symbol / insert_near_symbol 必须拒绝。
/// </param>
public sealed record SymbolSource(string Kind, string Name, string File, int StartLine, int EndLine, string Text, bool WholeLines = true);

/// <summary>
/// 智能体可用的 IDE 能力：编辑器缓冲区读写、编译、错误列表。实现负责线程切换，可在任意线程调用。
/// </summary>
public interface IIdeHost
{
    /// <summary>文件在编辑器中打开且有未保存修改时返回缓冲区文本；未打开或无未保存修改时返回 null。</summary>
    Task<string?> GetUnsavedTextAsync(string fullPath, CancellationToken cancellationToken);

    /// <summary>
    /// 文件在文本编辑器中打开时，在 UI 线程上一次完成：确认无未保存修改、缓冲区文本等于 <paramref name="expectedText"/>、
    /// 编辑器保存编码与 <paramref name="format"/> 一致，然后把差异部分替换为 <paramref name="newText"/>（进入编辑器撤销栈）并保存。
    /// 返回 true 表示已由编辑器写入磁盘；文件未在文本编辑器中打开或编码不一致时返回 false 且不做任何修改，由调用方写磁盘。
    /// 修改前发现条件不满足时抛出 <see cref="WorkspaceException"/>（保证未修改）；其他异常表示缓冲区可能已修改而保存失败。
    /// </summary>
    Task<bool> TryWriteOpenDocumentAsync(string fullPath, string expectedText, string newText, TextFormat format, CancellationToken cancellationToken);

    /// <summary>
    /// 经用户批准后编译整个解决方案并等待结束。用户拒绝、已有编译在进行或服务不可用时抛出 <see cref="WorkspaceException"/>。
    /// 取消时先请求 VS 取消编译并等待其结束（有上限），再抛出 OperationCanceledException。
    /// </summary>
    /// <param name="projectPath">要编译的项目文件完整路径（连同它引用的项目）；为 null 时编译整个解决方案。项目未在解决方案中时抛出 <see cref="WorkspaceException"/>。</param>
    Task<BuildOutcome> BuildSolutionAsync(string? projectPath, CancellationToken cancellationToken);

    /// <summary>返回错误列表中的错误与警告，错误在前，最多 <paramref name="maxItems"/> 条。</summary>
    Task<IReadOnlyList<IdeDiagnostic>> GetDiagnosticsAsync(int maxItems, CancellationToken cancellationToken);

    /// <summary>按名称（可带命名空间/类型前缀）查找解决方案中 C#/VB 的类型与成员声明，最多 <paramref name="maxItems"/> 条。</summary>
    Task<IReadOnlyList<CodeSymbol>> FindSymbolsAsync(string name, int maxItems, CancellationToken cancellationToken);

    /// <summary>查找名称匹配的符号的全部引用位置（每个匹配符号都查），最多 <paramref name="maxItems"/> 条。</summary>
    Task<IReadOnlyList<CodeSymbol>> FindReferencesAsync(string name, int maxItems, CancellationToken cancellationToken);

    /// <summary>返回文件中声明的类型与成员（按出现顺序，含嵌套深度）；文件不属于任何 C#/VB 项目时返回空列表。</summary>
    Task<IReadOnlyList<CodeSymbol>> GetOutlineAsync(string fullPath, CancellationToken cancellationToken);

    /// <summary>名称匹配的类型或成员的继承 / 实现关系，最多 <paramref name="maxItems"/> 条；<see cref="CodeSymbol.Kind"/> 为关系，<see cref="CodeSymbol.Text"/> 为匹配的符号。</summary>
    Task<IReadOnlyList<CodeSymbol>> GetHierarchyAsync(string name, int maxItems, CancellationToken cancellationToken);

    /// <summary>已加载解决方案中的项目及项目引用。</summary>
    Task<IReadOnlyList<ProjectNode>> GetProjectGraphAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 单个文件的编译器诊断（错误在前），不编译整个解决方案；<paramref name="text"/> 非空时按该文本检查。文件不属于已加载的 C#/VB 项目时返回 null。
    /// </summary>
    Task<IReadOnlyList<IdeDiagnostic>?> CheckFileAsync(string fullPath, string? text, int maxItems, CancellationToken cancellationToken);

    /// <summary>名称匹配的声明的源代码，每项最多 <paramref name="maxLines"/> 行。</summary>
    Task<IReadOnlyList<SymbolSource>> ReadSymbolAsync(string name, int maxLines, CancellationToken cancellationToken);

    /// <summary>名称匹配的成员的调用方（调用所在成员、位置与该行代码），最多 <paramref name="maxItems"/> 条。</summary>
    Task<IReadOnlyList<CodeSymbol>> GetCallersAsync(string name, int maxItems, CancellationToken cancellationToken);

    /// <summary>名称匹配的成员体内调用、创建或使用的成员，最多 <paramref name="maxItems"/> 条。</summary>
    Task<IReadOnlyList<CodeSymbol>> GetCalleesAsync(string name, int maxItems, CancellationToken cancellationToken);

    /// <summary>名称匹配的类型依赖的其他类型（继承、成员签名、成员体），最多 <paramref name="maxItems"/> 条。</summary>
    Task<IReadOnlyList<CodeSymbol>> GetTypeDependenciesAsync(string name, int maxItems, CancellationToken cancellationToken);
}
