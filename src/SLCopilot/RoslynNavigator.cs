using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using SLCopilot.Core;

namespace SLCopilot;

/// <summary>
/// 基于 Roslyn 当前解决方案快照的只读代码导航（仅 C#/VB 项目）。可在任意线程调用；不修改工作区。
/// </summary>
internal static class RoslynNavigator
{
    private const SymbolFilter Filter = SymbolFilter.TypeAndMember;

    /// <summary>
    /// 按名称查找声明。名称可带前缀（如 OrderService.Save）：按最后一段查找，再要求完整显示名以该名称结尾。
    /// <see cref="CodeSymbol.Text"/> 为“可访问性 签名 [项目]”。没有完全匹配时按模式（大小写不敏感、驼峰缩写）给出相近名称，Kind 以 suggestion: 开头。
    /// </summary>
    public static async Task<IReadOnlyList<CodeSymbol>> FindSymbolsAsync(Solution solution, string name, int maxItems, CancellationToken cancellationToken)
    {
        var results = new List<CodeSymbol>();
        var matches = await MatchAsync(solution, name, cancellationToken).ConfigureAwait(false);
        var suggestion = false;
        if (matches.Count == 0)
        {
            var last = LastSegment(name);
            if (last.Length == 0) return results;
            matches = (await SymbolFinder.FindSourceDeclarationsWithPatternAsync(solution, last, Filter, cancellationToken).ConfigureAwait(false))
                .Distinct(SymbolEqualityComparer.Default).OrderBy(s => s.Name.Length).ThenBy(s => s.ToDisplayString(QualifiedFormat), StringComparer.Ordinal).Take(20).ToList();
            suggestion = true;
        }
        foreach (var symbol in matches)
            foreach (var location in symbol.Locations.Where(l => l.IsInSource))
            {
                var span = location.GetLineSpan();
                var project = solution.GetDocument(location.SourceTree)?.Project.Name;
                var access = symbol.DeclaredAccessibility == Accessibility.NotApplicable ? "" : symbol.DeclaredAccessibility.ToString().ToLowerInvariant() + " ";
                results.Add(new((suggestion ? "suggestion:" : "") + Kind(symbol), symbol.ToDisplayString(QualifiedFormat), span.Path, span.StartLinePosition.Line + 1,
                    access + symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) + (project is null ? "" : $" [{project}]")));
                if (results.Count >= maxItems) return results;
            }
        return results;
    }

    /// <summary>
    /// 声明的源代码（最多前 3 个匹配符号；分部类型每个部分一项）：从声明前的文档注释开始，到声明结束；超过 <paramref name="maxLines"/> 行时截断。
    /// 只返回源文件中的声明。
    /// </summary>
    public static async Task<IReadOnlyList<SymbolSource>> ReadSymbolAsync(Solution solution, string name, int maxLines, CancellationToken cancellationToken)
    {
        var results = new List<SymbolSource>();
        var matched = (await MatchAsync(solution, name, cancellationToken).ConfigureAwait(false)).AsEnumerable();
        // 名称带参数列表（如 OrderService.Save(int)）时先按参数列表筛选重载，再取前 3 个，避免同名重载较多时目标被截掉。
        if (name.IndexOf('(') is var paren and >= 0)
        {
            var wanted = Compact(name.Substring(paren));
            matched = matched.Where(s => s.ToDisplayString(QualifiedFormat) is var display && display.IndexOf('(') is var p and >= 0 && Compact(display.Substring(p)) == wanted);
        }
        // 多目标框架或共享项目中同一份源码在每个编译中各有一个符号：按（文件, 起止位置）去重，避免同一声明被当成多个。
        var seen = new HashSet<(string, int, int)>();
        var symbols = 0;
        foreach (var symbol in matched.Take(30))
        {
            if (symbols >= 3) break;
            var added = false;
            foreach (var reference in symbol.DeclaringSyntaxReferences.Take(5))
            {
                var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                // VB 的方法、属性等声明节点是块的开头语句（Sub Foo()），整个成员是该块（到 End Sub）。只在语句是块的第一个子节点时上溯：
                // 自动属性、接口成员、MustOverride 方法、Event、Declare 等没有自己的块，父节点是类型或命名空间块，不能上溯（否则会替换整个类型）。
                if (node.Language == LanguageNames.VisualBasic && node.GetType().Name.EndsWith("StatementSyntax", StringComparison.Ordinal)
                    && node.Parent is { } block && block.GetType().Name.EndsWith("BlockSyntax", StringComparison.Ordinal) && block.ChildNodes().FirstOrDefault() == node)
                    node = block;
                // 字段与事件字段的声明节点是变量声明符：向上取整条字段声明（C# FieldDeclaration / EventFieldDeclaration，VB FieldDeclaration）。
                if (symbol is IFieldSymbol or IEventSymbol)
                    for (var (ancestor, depth) = (node.Parent, 0); ancestor is not null && depth < 3; ancestor = ancestor.Parent, depth++)
                        if (ancestor.GetType().Name.EndsWith("FieldDeclarationSyntax", StringComparison.Ordinal)) { node = ancestor; break; }
                var text = await node.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                var start = DeclarationStart(node);
                var startLine = text.Lines.GetLineFromPosition(start).LineNumber;
                var endLine = text.Lines.GetLineFromPosition(node.Span.End).LineNumber;
                if (!seen.Add((node.SyntaxTree.FilePath, start, node.Span.End))) continue;
                var lastLine = Math.Min(endLine, startLine + maxLines - 1);
                var body = text.ToString(TextSpan.FromBounds(text.Lines[startLine].Start, text.Lines[lastLine].End));
                if (lastLine < endLine) body += $"\n[... truncated: the declaration continues to line {endLine + 1}; read the rest with read_file]";
                results.Add(new(Kind(symbol), symbol.ToDisplayString(QualifiedFormat), node.SyntaxTree.FilePath, startLine + 1, endLine + 1, body, OwnsLines(node, start, text)));
                added = true;
            }
            if (added) symbols++;
        }
        return results;
    }

    /// <summary>
    /// 声明是否独占所在的行：首行在声明起点前只有空白，末行在声明结束后只有空白或行尾注释，且字段 / 事件声明只声明了一个变量。
    /// </summary>
    internal static bool OwnsLines(SyntaxNode node, int start, SourceText text)
    {
        var first = text.Lines.GetLineFromPosition(start);
        if (text.ToString(TextSpan.FromBounds(first.Start, start)).Trim().Length > 0) return false;
        var last = text.Lines.GetLineFromPosition(node.Span.End);
        var rest = text.ToString(TextSpan.FromBounds(node.Span.End, last.End)).Trim();
        if (rest.Length > 0 && !rest.StartsWith("//", StringComparison.Ordinal) && !rest.StartsWith("'", StringComparison.Ordinal)) return false;
        // 多变量字段（int a, b; / Dim a, b As Integer）：整条声明属于多个符号。只数声明本身的变量（初始化表达式中的局部变量不算）：
        // C# 为 FieldDeclaration → VariableDeclaration → VariableDeclarator，VB 为 FieldDeclaration → VariableDeclarator → ModifiedIdentifier。
        if (!node.GetType().Name.EndsWith("FieldDeclarationSyntax", StringComparison.Ordinal)) return true;
        var names = node.Language == LanguageNames.VisualBasic
            ? node.ChildNodes().Where(n => n.GetType().Name == "VariableDeclaratorSyntax").SelectMany(d => d.ChildNodes()).Count(n => n.GetType().Name == "ModifiedIdentifierSyntax")
            : node.ChildNodes().SelectMany(c => c.ChildNodes()).Count(n => n.GetType().Name == "VariableDeclaratorSyntax");
        return names <= 1;
    }

    /// <summary>
    /// 与语言无关的限定名显示：C# 与 VB 都为“命名空间.类型.成员(参数类型)”，用于名称匹配、重载筛选与结果显示。
    /// VB 的默认显示格式带修饰符与关键字（如 “Public Sub Save()”），不能用于按限定名匹配。
    /// </summary>
    private static readonly SymbolDisplayFormat QualifiedFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static string Compact(string value) => new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// 声明的起点：紧贴声明的文档注释与普通注释（与声明之间没有空行）计入声明，特性本身属于声明节点；
    /// 预处理指令（#region、#if 等）与隔着空行的注释不计入，替换或删除成员时不会动到它们。
    /// </summary>
    internal static int DeclarationStart(SyntaxNode node)
    {
        var start = node.Span.Start;
        var leading = node.GetLeadingTrivia();
        var newlines = 0;
        for (var i = leading.Count - 1; i >= 0; i--)
        {
            var trivia = leading[i];
            if (trivia.IsDirective || trivia.ToFullString().TrimStart().StartsWith("#", StringComparison.Ordinal)) break;
            var content = trivia.ToFullString();
            if (content.Trim().Length == 0)
            {
                // 前导 trivia 中单独的换行表示空行（上一行的换行属于上一个记号的尾随 trivia）；注释行自身的换行之后再遇到换行即为空行。
                newlines += content.Count(c => c == '\n');
                if (newlines >= 2 || (newlines >= 1 && i == 0)) break;
                continue;
            }
            newlines = 0;
            start = trivia.FullSpan.Start;
        }
        return start;
    }

    /// <summary>调用方（最多前 10 个匹配符号）：调用所在的成员、文件、行号与该行代码。</summary>
    public static async Task<IReadOnlyList<CodeSymbol>> GetCallersAsync(Solution solution, string name, int maxItems, CancellationToken cancellationToken)
    {
        var results = new List<CodeSymbol>();
        foreach (var symbol in (await MatchAsync(solution, name, cancellationToken).ConfigureAwait(false)).Take(10))
            foreach (var caller in await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
                foreach (var location in caller.Locations.Where(l => l.IsInSource))
                {
                    var span = location.GetLineSpan();
                    var text = await location.SourceTree!.GetTextAsync(cancellationToken).ConfigureAwait(false);
                    var line = span.StartLinePosition.Line;
                    results.Add(new(caller.IsDirect ? "caller" : "indirect-caller", caller.CallingSymbol.ToDisplayString(QualifiedFormat), span.Path, line + 1,
                        $"calls {symbol.ToDisplayString(QualifiedFormat)}: " + (line < text.Lines.Count ? text.Lines[line].ToString().Trim() : "")));
                    if (results.Count >= maxItems) return results;
                }
        return results;
    }

    /// <summary>
    /// 成员体内调用或使用的成员（最多前 5 个匹配成员）：方法调用（calls）、对象创建（creates）、属性 / 字段 / 事件的使用（uses）。
    /// 每个被调用成员只列一次；File / Line 为被调用成员的声明位置（框架成员为空），Text 为首次调用所在行。
    /// </summary>
    public static async Task<IReadOnlyList<CodeSymbol>> GetCalleesAsync(Solution solution, string name, int maxItems, CancellationToken cancellationToken)
    {
        var results = new List<CodeSymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var symbol in (await MatchAsync(solution, name, cancellationToken).ConfigureAwait(false)).Where(s => s is IMethodSymbol or IPropertySymbol or IEventSymbol).Take(5))
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                if (solution.GetDocument(node.SyntaxTree) is not { } document || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model) continue;
                var text = await node.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);
                foreach (var child in node.DescendantNodesAndSelf())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (relation, target) = model.GetOperation(child, cancellationToken) switch
                    {
                        IInvocationOperation invocation => ("calls", (ISymbol?)invocation.TargetMethod),
                        IObjectCreationOperation creation => ("creates", creation.Constructor?.ContainingType),
                        IPropertyReferenceOperation property => ("uses", property.Property),
                        IFieldReferenceOperation field => ("uses", field.Field),
                        IEventReferenceOperation @event => ("uses", @event.Event),
                        _ => ("", null),
                    };
                    if (target is null || !seen.Add(target.OriginalDefinition)) continue;
                    var declared = target.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
                    var line = text.Lines.GetLineFromPosition(child.SpanStart);
                    results.Add(new(relation, target.OriginalDefinition.ToDisplayString(QualifiedFormat), declared?.Path ?? "", declared is { } d ? d.StartLinePosition.Line + 1 : 0,
                        $"in {symbol.ToDisplayString(QualifiedFormat)} at line {line.LineNumber + 1}: {line.ToString().Trim()}"));
                    if (results.Count >= maxItems) return results;
                }
            }
        return results;
    }

    /// <summary>
    /// 类型依赖的其他类型（最多前 3 个匹配类型）：继承（inherits）、成员签名中的类型（signature：字段、属性、参数、返回值、事件）、
    /// 成员体内创建或调用的类型（body）。去掉基元类型与 object / string 等特殊类型，泛型展开为类型参数。每个类型只列一次。
    /// </summary>
    public static async Task<IReadOnlyList<CodeSymbol>> GetTypeDependenciesAsync(Solution solution, string name, int maxItems, CancellationToken cancellationToken)
    {
        var results = new List<CodeSymbol>();
        foreach (var type in (await MatchAsync(solution, name, cancellationToken).ConfigureAwait(false)).OfType<INamedTypeSymbol>().Take(3))
        {
            var found = new Dictionary<ITypeSymbol, string>(SymbolEqualityComparer.Default);
            void Add(ITypeSymbol? candidate, string relation)
            {
                switch (candidate)
                {
                    case null: return;
                    case IArrayTypeSymbol array: Add(array.ElementType, relation); return;
                    case INamedTypeSymbol named when named.IsGenericType:
                        foreach (var argument in named.TypeArguments) Add(argument, relation);
                        candidate = named.ConstructedFrom;
                        break;
                }
                if (candidate is ITypeParameterSymbol || candidate.SpecialType != SpecialType.None || SymbolEqualityComparer.Default.Equals(candidate, type)) return;
                if (!found.ContainsKey(candidate)) found[candidate] = relation;
            }
            Add(type.BaseType, "inherits");
            foreach (var @interface in type.Interfaces) Add(@interface, "inherits");
            foreach (var member in type.GetMembers().Where(m => !m.IsImplicitlyDeclared))
                switch (member)
                {
                    case IFieldSymbol field: Add(field.Type, "signature"); break;
                    case IPropertySymbol property: Add(property.Type, "signature"); foreach (var p in property.Parameters) Add(p.Type, "signature"); break;
                    case IEventSymbol @event: Add(@event.Type, "signature"); break;
                    case IMethodSymbol method:
                        Add(method.ReturnType, "signature");
                        foreach (var p in method.Parameters) Add(p.Type, "signature");
                        break;
                }
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                var node = await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false);
                if (solution.GetDocument(node.SyntaxTree) is not { } document || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model) continue;
                foreach (var child in node.DescendantNodes())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    switch (model.GetOperation(child, cancellationToken))
                    {
                        case IObjectCreationOperation creation: Add(creation.Type, "body"); break;
                        case IInvocationOperation invocation when invocation.TargetMethod.IsStatic: Add(invocation.TargetMethod.ContainingType, "body"); break;
                        case IInvocationOperation invocation: Add(invocation.Instance?.Type, "body"); break;
                    }
                }
            }
            foreach (var pair in found.OrderBy(p => p.Value switch { "inherits" => 0, "signature" => 1, _ => 2 }).ThenBy(p => p.Key.ToDisplayString(QualifiedFormat), StringComparer.Ordinal))
            {
                var declared = pair.Key.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
                results.Add(new(pair.Value, pair.Key.ToDisplayString(QualifiedFormat), declared?.Path ?? "", declared is { } d ? d.StartLinePosition.Line + 1 : 0, type.ToDisplayString(QualifiedFormat)));
                if (results.Count >= maxItems) return results;
            }
        }
        return results;
    }

    /// <summary>
    /// 查找全部匹配符号的引用（最多前 10 个匹配符号），按文件与行排序；<see cref="CodeSymbol.Text"/> 为“[所在成员] 该行代码”。
    /// </summary>
    public static async Task<IReadOnlyList<CodeSymbol>> FindReferencesAsync(Solution solution, string name, int maxItems, CancellationToken cancellationToken)
    {
        var results = new List<CodeSymbol>();
        var models = new Dictionary<DocumentId, SemanticModel?>();
        foreach (var symbol in (await MatchAsync(solution, name, cancellationToken).ConfigureAwait(false)).Take(10))
        {
            var referenced = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);
            var locations = referenced.SelectMany(r => r.Locations)
                .OrderBy(r => r.Document.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Location.SourceSpan.Start);
            foreach (var reference in locations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var span = reference.Location.GetLineSpan();
                var text = await reference.Document.GetTextAsync(cancellationToken).ConfigureAwait(false);
                if (!models.TryGetValue(reference.Document.Id, out var model))
                    models[reference.Document.Id] = model = await reference.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var container = model?.GetEnclosingSymbol(reference.Location.SourceSpan.Start, cancellationToken);
                var line = span.StartLinePosition.Line;
                results.Add(new("reference", symbol.ToDisplayString(QualifiedFormat), span.Path, line + 1,
                    (container is null ? "" : $"[{container.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}] ") +
                    (line < text.Lines.Count ? text.Lines[line].ToString().Trim() : "")));
                if (results.Count >= maxItems) return results;
            }
        }
        return results;
    }

    /// <summary>文件中声明的类型与成员（不进入方法体，不含局部变量），按位置排序。</summary>
    public static async Task<IReadOnlyList<CodeSymbol>> GetOutlineAsync(Solution solution, string fullPath, CancellationToken cancellationToken)
    {
        var id = solution.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault();
        if (id is null || solution.GetDocument(id) is not { } document) return [];
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null) return [];
        var results = new List<CodeSymbol>();
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var node in root.DescendantNodes(n => !IsBody(model.GetDeclaredSymbol(n, cancellationToken))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (model.GetDeclaredSymbol(node, cancellationToken) is not { } symbol || !IsOutlineSymbol(symbol) || !seen.Add(symbol)) continue;
            var depth = 0;
            for (var container = symbol.ContainingType; container is not null; container = container.ContainingType) depth++;
            results.Add(new(Kind(symbol), symbol.Name, fullPath, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), depth));
        }
        return results;
    }

    /// <summary>
    /// 继承与实现关系（最多前 10 个匹配符号）。类型：基类链（不含 object）、实现的全部接口、派生类（传递）、接口的派生接口与实现类型；
    /// 成员：重写 / 实现的上层成员，以及重写它、实现它的成员。框架等元数据中的符号没有源文件，<see cref="CodeSymbol.File"/> 为空。
    /// <see cref="CodeSymbol.Kind"/> 为关系（base、interface、derived、implementation、overrides、implements、overridden-by、implemented-by），
    /// <see cref="CodeSymbol.Text"/> 为匹配到的符号。
    /// </summary>
    public static async Task<IReadOnlyList<CodeSymbol>> GetHierarchyAsync(Solution solution, string name, int maxItems, CancellationToken cancellationToken)
    {
        var results = new List<CodeSymbol>();
        foreach (var symbol in (await MatchAsync(solution, name, cancellationToken).ConfigureAwait(false)).Take(10))
        {
            var subject = symbol.ToDisplayString(QualifiedFormat);
            var related = new List<(string Relation, ISymbol Symbol)>();
            if (symbol is INamedTypeSymbol type)
            {
                for (var baseType = type.BaseType; baseType is not null && baseType.SpecialType != SpecialType.System_Object; baseType = baseType.BaseType)
                    related.Add(("base", baseType));
                related.AddRange(type.AllInterfaces.Select(i => ("interface", (ISymbol)i)));
                if (type.TypeKind == TypeKind.Interface)
                {
                    related.AddRange((await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Select(t => ("derived", (ISymbol)t)));
                    related.AddRange((await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Select(t => ("implementation", (ISymbol)t)));
                }
                else if (type.TypeKind == TypeKind.Class)
                    related.AddRange((await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Select(t => ("derived", (ISymbol)t)));
            }
            else if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol)
            {
                if (Overridden(symbol) is { } overridden) related.Add(("overrides", overridden));
                if (symbol.ContainingType is { } container)
                    foreach (var member in container.AllInterfaces.SelectMany(i => i.GetMembers()))
                        if (SymbolEqualityComparer.Default.Equals(container.FindImplementationForInterfaceMember(member), symbol)) related.Add(("implements", member));
                related.AddRange((await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false))
                    .Select(m => ("overridden-by", m)));
                if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
                    related.AddRange((await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken).ConfigureAwait(false))
                        .Select(m => ("implemented-by", m)));
            }
            foreach (var (relation, target) in related)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var location = target.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan();
                results.Add(new(relation, target.ToDisplayString(QualifiedFormat), location?.Path ?? "", location is { } span ? span.StartLinePosition.Line + 1 : 0, subject));
                if (results.Count >= maxItems) return results;
            }
        }
        return results;
    }

    /// <summary>
    /// 单个文件的编译器诊断（错误与警告，不含分析器），不需要编译整个解决方案。<paramref name="text"/> 非空时按该文本（如刚写入磁盘的内容）检查，
    /// 否则按工作区当前快照。文件不属于任何已加载项目时返回 null。多目标项目中同一文件只按前两个目标检查，相同诊断只列一次。
    /// </summary>
    public static async Task<IReadOnlyList<IdeDiagnostic>?> CheckFileAsync(Solution solution, string fullPath, string? text, int maxItems, CancellationToken cancellationToken)
    {
        var ids = solution.GetDocumentIdsWithFilePath(fullPath);
        if (ids.IsEmpty) return null;
        var results = new List<IdeDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids.Take(2))
        {
            if (solution.GetDocument(id) is not { } document) continue;
            if (text is not null) document = document.WithText(SourceText.From(text));
            if (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model) continue;
            foreach (var diagnostic in model.GetDiagnostics(cancellationToken: cancellationToken)
                         .Where(d => d.Severity >= DiagnosticSeverity.Warning).OrderByDescending(d => d.Severity).ThenBy(d => d.Location.SourceSpan.Start))
            {
                var span = diagnostic.Location.GetLineSpan();
                var message = $"{diagnostic.Id}: {diagnostic.GetMessage()}";
                if (!seen.Add(span.StartLinePosition.Line + "|" + message)) continue;
                results.Add(new(diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning", fullPath, span.StartLinePosition.Line + 1,
                    span.StartLinePosition.Character + 1, document.Project.Name, message));
                if (results.Count >= maxItems) return results;
            }
        }
        return results;
    }

    /// <summary>解决方案中的项目（C#/VB 等 Roslyn 项目；多目标项目每个目标框架一项）及其项目引用。</summary>
    public static IReadOnlyList<ProjectNode> GetProjectGraph(Solution solution) => solution.Projects
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        .Select(p => new ProjectNode(p.Name, p.Language, p.FilePath ?? "", p.DocumentIds.Count,
            p.ProjectReferences.Select(r => solution.GetProject(r.ProjectId)?.Name).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
            p.AssemblyName, p.CompilationOptions?.OutputKind.ToString() ?? ""))
        .ToList();

    private static ISymbol? Overridden(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.OverriddenMethod,
        IPropertySymbol property => property.OverriddenProperty,
        IEventSymbol @event => @event.OverriddenEvent,
        _ => null,
    };

    private static string LastSegment(string name)
    {
        var parts = name.Split(['.'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "" : parts[parts.Length - 1].Split('(')[0].Split('<')[0].Trim();
    }

    private static async Task<IReadOnlyList<ISymbol>> MatchAsync(Solution solution, string name, CancellationToken cancellationToken)
    {
        var parts = name.Split(['.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return [];
        var last = LastSegment(name);
        if (last.Length == 0) return [];
        var symbols = await SymbolFinder.FindSourceDeclarationsAsync(solution, n => n == last, Filter, cancellationToken).ConfigureAwait(false);
        return symbols
            .Where(s => parts.Length == 1 || ("." + s.ToDisplayString(QualifiedFormat).Split('(')[0]).EndsWith("." + string.Join(".", parts).Split('(')[0], StringComparison.Ordinal))
            .Distinct(SymbolEqualityComparer.Default)
            .OrderBy(s => s.ToDisplayString(QualifiedFormat), StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsBody(ISymbol? symbol) => symbol is IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol;

    private static bool IsOutlineSymbol(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => true,
        IMethodSymbol method => method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor
            or MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.Destructor,
        IPropertySymbol or IFieldSymbol or IEventSymbol => !symbol.IsImplicitlyDeclared,
        _ => false,
    };

    private static string Kind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind.ToString().ToLowerInvariant(),
        IMethodSymbol method => method.MethodKind == MethodKind.Constructor ? "constructor" : "method",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };
}
