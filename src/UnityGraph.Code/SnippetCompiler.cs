using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace UnityGraph.Code;

/// <summary>What `ug eval` compiles against: reference files, script defines and C# version.</summary>
public sealed record SnippetSettings(IReadOnlyList<string> References, IReadOnlyList<string> Defines, string LanguageVersion)
{
    /// <summary>Seconds the loop guard lets the main thread stay away from the editor loop; 0 leaves loops as written.</summary>
    public double GuardSeconds { get; init; }

    /// <summary>`Type.Method` a file of types starts from; null takes its one static Run or Main.</summary>
    public string? Entry { get; init; }
}

/// <summary>A compile error at the code's own line and column (1-based; 0 when it has no place in the code).</summary>
public sealed record SnippetError(int Line, int Column, string Code, string Message);

/// <summary>A compiled snippet, or its errors, or why it has nothing to run.</summary>
public sealed class SnippetBuild
{
    public IReadOnlyList<SnippetError> Errors { get; init; } = [];

    /// <summary>Not a compile error but nothing to run: no entry, several, no code.</summary>
    public string? Problem { get; init; }

    public byte[]? Assembly { get; init; }

    public byte[]? Pdb { get; init; }

    /// <summary>The assembly's name: <see cref="SnippetCompiler.AssemblyPrefix"/> and a new GUID for every compile.</summary>
    public string? Name { get; init; }

    /// <summary>The entry's type as `Assembly.GetType` takes it (`Ns.Outer+Inner`).</summary>
    public string? EntryType { get; init; }

    public string? EntryMethod { get; init; }

    /// <summary>The code's statements are the entry (a generated method that takes no arguments).</summary>
    public bool Statements { get; init; }

    /// <summary>The statements give a value: a `return x;` or a last expression that is not void.</summary>
    public bool HasResult { get; init; }

    public bool Ok => Problem == null && Errors.Count == 0;
}

/// <summary>
/// Compiles `ug eval` code (SPEC-editor-link, Running code): a bare expression, statements (C# 9
/// top-level statements, in any order with types) or a file of types, wrapped in a generated class
/// with the default usings. Every piece of the code is placed under a `#line` directive at its own
/// line and column, so errors and stack frames name the code's lines, never the wrapper's.
/// </summary>
public static class SnippetCompiler
{
    /// <summary>The generated class and method the statements run in.</summary>
    public const string WrapperType = "__UgSnippet";
    public const string WrapperMethod = "Run";
    public const string GuardType = "UnityGraph.Bridge.EvalGuard";

    /// <summary>Every compiled snippet's name starts with it; the bridge leaves those out of its references.</summary>
    public const string AssemblyPrefix = "ug-eval-";

    private static readonly string[] _usings =
    [
        "System", "System.Collections", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks",
        "UnityEngine", "UnityEditor", "UnityEngine.SceneManagement",
    ];

    private static readonly (string Name, string Target)[] _aliases = [("Object", "UnityEngine.Object"), ("Random", "UnityEngine.Random")];

    // Kept for the process (a `ug batch` of evals): Roslyn reads a reference's metadata once per
    // reference object, so a new object per compile reads every file again.
    private static readonly ConcurrentDictionary<string, (DateTime Written, MetadataReference Reference)> _references = new(StringComparer.OrdinalIgnoreCase);

    private enum Body
    {
        Value,
        Void,
        AsyncValue,
        AsyncVoid,
        Iterator,
    }

    /// <summary>Compiles <paramref name="code"/>; <paramref name="name"/> is what errors and frames call it (`snippet`, `Tools/x.cs`).</summary>
    public static SnippetBuild Compile(string code, string name, SnippetSettings settings, bool emit = true)
    {
        string text = code.ReplaceLineEndings("\n");
        if (text.Trim().Length == 0)
        {
            return new SnippetBuild { Problem = "no code" };
        }

        var parse = new CSharpParseOptions(
            LanguageVersionFacts.TryParse(settings.LanguageVersion, out LanguageVersion version) ? version : LanguageVersion.CSharp9,
            DocumentationMode.None,
            SourceCodeKind.Regular,
            settings.Defines);
        Parts parts = Split(text, parse);
        if (parts.Statements.Count > 0 && settings.Entry != null)
        {
            return new SnippetBuild { Problem = "the code has statements, which run as its entry; --entry is for a file of types" };
        }

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Debug,
            allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Disable);
        List<MetadataReference> references = [.. settings.References.Where(File.Exists).Select(Reference)];
        string assemblyName = AssemblyPrefix + Guid.NewGuid().ToString("N");

        bool candidate = parts.Candidate != null && !parts.Yields;
        Body body = BodyOf(parts, candidate);
        (SyntaxTree tree, int at) = Generate(parts, name, parse, body, candidate);
        CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, [tree], references, options);

        // A last expression whose type is void (a call like Debug.Log) is a statement, not the result.
        if (candidate && at >= 0)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            ExpressionSyntax? returned = tree.GetRoot().FindToken(at).Parent?.AncestorsAndSelf().OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;
            if (returned != null && model.GetTypeInfo(returned).Type is { SpecialType: SpecialType.System_Void })
            {
                candidate = false;
                body = BodyOf(parts, candidate);
                (tree, _) = Generate(parts, name, parse, body, candidate);
                compilation = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees[0], tree);
            }
        }

        List<SnippetError> errors = Errors(compilation.GetDiagnostics(), name);
        if (errors.Count > 0)
        {
            return new SnippetBuild { Errors = errors };
        }

        bool statements = parts.Statements.Count > 0;
        string? entryType = WrapperType;
        string entryMethod = WrapperMethod;
        if (!statements)
        {
            string? problem = FindEntry(compilation, tree, settings.Entry, out entryType, out entryMethod);
            if (problem != null)
            {
                return new SnippetBuild { Problem = problem };
            }
        }

        var build = new SnippetBuild
        {
            EntryType = entryType,
            EntryMethod = entryMethod,
            Statements = statements,
            HasResult = statements && (candidate || parts.ReturnsValue),
        };
        if (!emit)
        {
            return build;
        }

        if (settings.GuardSeconds > 0 && compilation.GetTypeByMetadataName(GuardType) != null)
        {
            var guard = new LoopGuard(settings.GuardSeconds);
            compilation = compilation.ReplaceSyntaxTree(tree, tree.WithRootAndOptions(guard.Visit(tree.GetRoot()), tree.Options));
        }

        using var assembly = new MemoryStream();
        using var pdb = new MemoryStream();
        EmitResult result = compilation.Emit(assembly, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!result.Success)
        {
            return new SnippetBuild { Errors = Errors(result.Diagnostics, name) };
        }

        return new SnippetBuild
        {
            Assembly = assembly.ToArray(),
            Pdb = pdb.ToArray(),
            Name = assemblyName,
            EntryType = build.EntryType,
            EntryMethod = build.EntryMethod,
            Statements = build.Statements,
            HasResult = build.HasResult,
        };
    }

    /// <summary>The C# version Unity compiles scripts with: 9.0 from 2021.2 (Unity 6 too), 8.0 from 2020.2, 7.3 before.</summary>
    public static string LanguageFor(string unityVersion)
    {
        string[] parts = unityVersion.Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(new string([.. parts[1].TakeWhile(char.IsDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        return (major, minor) switch
        {
            _ when major > 2021 || (major == 2021 && minor >= 2) => "9.0",
            _ when major > 2020 || (major == 2020 && minor >= 2) => "8.0",
            _ => "7.3",
        };
    }

    // A file rewritten since (a script compile) is read again.
    private static MetadataReference Reference(string path)
    {
        DateTime written = File.GetLastWriteTimeUtc(path);
        if (_references.TryGetValue(path, out var cached) && cached.Written == written)
        {
            return cached.Reference;
        }

        MetadataReference reference = MetadataReference.CreateFromFile(path);
        _references[path] = (written, reference);
        return reference;
    }

    // ---- splitting the code into pieces ----

    private sealed class Parts
    {
        public required SourceText Source;
        public List<SyntaxNode> Usings = [];
        public List<SyntaxNode> Attributes = [];
        public List<StatementSyntax> Statements = [];
        public List<SyntaxNode> Members = [];
        public List<SyntaxNode> Types = [];

        /// <summary>Directives and comments after the last piece (an `#endif`), placed after the group that piece is in.</summary>
        public TextSpan Tail;
        public int TailAfter;

        /// <summary>The last statement when it may be the result: an expression that is not an assignment or ++/--.</summary>
        public ExpressionSyntax? Candidate;

        /// <summary>Where the result expression ends in the code.</summary>
        public int CandidateEnd;

        /// <summary>The last statement's text, when it is not its own node in the code: a bare expression, or a misread member.</summary>
        public TextSpan? LastSpan;

        /// <summary>The last statement's `;` is missing (allowed for the last one only).</summary>
        public bool OpenEnd;

        /// <summary>The whole code is one expression (no statement around it).</summary>
        public bool Bare;
        public bool Awaits;
        public bool Yields;
        public bool ReturnsValue;
    }

    private static Parts Split(string text, CSharpParseOptions parse)
    {
        var source = SourceText.From(text);
        ExpressionSyntax expression = SyntaxFactory.ParseExpression(text, options: parse);
        if (!expression.ContainsDiagnostics && expression.FullSpan.Length == text.Length)
        {
            var bare = new Parts { Source = source, Bare = true, LastSpan = new TextSpan(0, text.Length), OpenEnd = true, CandidateEnd = text.Length };
            bare.Statements.Add(SyntaxFactory.ExpressionStatement(expression));
            bare.Candidate = IsValue(expression) ? expression : null;
            bare.Awaits = Scan(expression).Any(n => n is AwaitExpressionSyntax);
            return bare;
        }

        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(source, parse).GetRoot();
        var parts = new Parts { Source = source };
        parts.Usings.AddRange(root.Externs);
        parts.Usings.AddRange(root.Usings);
        parts.Attributes.AddRange(root.AttributeLists);
        foreach (MemberDeclarationSyntax member in root.Members)
        {
            switch (member)
            {
                case GlobalStatementSyntax { Statement: LocalDeclarationStatementSyntax local } when member == root.Members.Last() && Misread(local):
                    // `a` after other statements parses as a declaration of type `a` missing its name: it is an expression.
                    parts.Statements.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.ParseExpression(local.Declaration.Type.ToString(), options: parse)));
                    parts.LastSpan = TextSpan.FromBounds(local.FullSpan.Start, local.Declaration.Type.Span.End);
                    parts.CandidateEnd = local.Declaration.Type.Span.End;
                    parts.OpenEnd = true;
                    break;
                case GlobalStatementSyntax statement:
                    parts.Statements.Add(statement.Statement);
                    break;
                case BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    parts.Types.Add(member);
                    break;
                case IncompleteMemberSyntax incomplete when incomplete == root.Members.Last() && incomplete.AttributeLists.Count == 0 && incomplete.Modifiers.Count == 0 && incomplete.Type != null:
                    // `go.name` after other statements parses as a member missing its name: it is an expression.
                    ExpressionSyntax read = SyntaxFactory.ParseExpression(incomplete.Type.ToString(), options: parse);
                    parts.Statements.Add(SyntaxFactory.ExpressionStatement(read));
                    parts.LastSpan = TextSpan.FromBounds(incomplete.FullSpan.Start, incomplete.Type.Span.End);
                    parts.CandidateEnd = incomplete.Type.Span.End;
                    parts.OpenEnd = true;
                    break;
                default:
                    parts.Members.Add(member);
                    break;
            }
        }

        parts.Tail = root.EndOfFileToken.FullSpan;
        MemberDeclarationSyntax? final = root.Members.LastOrDefault();
        parts.TailAfter = final is GlobalStatementSyntax || (final is IncompleteMemberSyntax && parts.LastSpan != null) ? 0 : final is BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax ? 2 : 1;

        foreach (StatementSyntax statement in parts.Statements)
        {
            foreach (SyntaxNode node in Scan(statement))
            {
                parts.Awaits |= node is AwaitExpressionSyntax
                    || (node is CommonForEachStatementSyntax loop && loop.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
                    || (node is UsingStatementSyntax use && use.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
                    || (node is LocalDeclarationStatementSyntax local && local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword));
                parts.Yields |= node is YieldStatementSyntax;
                parts.ReturnsValue |= node is ReturnStatementSyntax { Expression: not null };
            }
        }

        if (parts.Statements.Count > 0 && parts.LastSpan == null)
        {
            parts.OpenEnd = parts.Statements[^1].GetLastToken(includeZeroWidth: true) is { IsMissing: true } end && end.IsKind(SyntaxKind.SemicolonToken);
        }

        if (parts.Statements.Count > 0 && parts.Statements[^1] is ExpressionStatementSyntax last && IsValue(last.Expression))
        {
            parts.Candidate = last.Expression;
            if (parts.LastSpan == null)
            {
                parts.CandidateEnd = last.Expression.Span.End;
            }
        }

        return parts;
    }

    private static bool Misread(LocalDeclarationStatementSyntax local) =>
        local.Modifiers.Count == 0 && !local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) && !local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
        && local.Declaration.Variables.Count == 1 && local.Declaration.Variables[0] is { Identifier.IsMissing: true, Initializer: null }
        && local.SemicolonToken.IsMissing;

    // The statements' own nodes: not inside a lambda, an anonymous method or a local function, whose
    // awaits, yields and returns are their own.
    private static IEnumerable<SyntaxNode> Scan(SyntaxNode node) =>
        node.DescendantNodesAndSelf(n => n == node || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax));

    private static bool IsValue(ExpressionSyntax expression) =>
        expression is not (AssignmentExpressionSyntax
            or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression or (int)SyntaxKind.PreDecrementExpression }
            or PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression or (int)SyntaxKind.PostDecrementExpression });

    private static Body BodyOf(Parts parts, bool candidate) =>
        parts.Yields ? Body.Iterator
        : parts.Awaits ? (candidate || parts.ReturnsValue ? Body.AsyncValue : Body.AsyncVoid)
        : !parts.ReturnsValue && !candidate ? Body.Void
        : Body.Value;

    // ---- the generated file ----

    // The code's pieces in a file around the wrapper; `at` is where the returned last expression starts, or -1.
    private static (SyntaxTree Tree, int At) Generate(Parts parts, string name, CSharpParseOptions parse, Body body, bool candidate)
    {
        var file = new StringBuilder();
        string label = name.Replace('\\', '/').Replace("\"", "", StringComparison.Ordinal);
        foreach (SyntaxNode piece in parts.Usings.Where(u => u is ExternAliasDirectiveSyntax))
        {
            Place(file, parts.Source, piece.FullSpan, label);
        }

        file.Append("#line hidden\n");
        HashSet<string> ownAliases = [.. parts.Usings.OfType<UsingDirectiveSyntax>().Where(u => u.Alias != null).Select(u => u.Alias!.Name.Identifier.ValueText)];
        foreach (string space in _usings)
        {
            file.Append("using ").Append(space).Append(";\n");
        }

        foreach ((string alias, string target) in _aliases.Where(a => !ownAliases.Contains(a.Name)))
        {
            file.Append("using ").Append(alias).Append(" = ").Append(target).Append(";\n");
        }

        foreach (SyntaxNode piece in parts.Usings.Where(u => u is UsingDirectiveSyntax).Concat(parts.Attributes))
        {
            Place(file, parts.Source, piece.FullSpan, label);
        }

        file.Append("#line hidden\npublic static class ").Append(WrapperType).Append("\n{\n");
        foreach (SyntaxNode member in parts.Members)
        {
            Place(file, parts.Source, member.FullSpan, label);
        }

        Tail(file, parts, 1, label);

        int at = -1;
        if (parts.Statements.Count > 0)
        {
            string signature = body switch
            {
                Body.Void => "void",
                Body.AsyncValue => "async System.Threading.Tasks.Task<object>",
                Body.AsyncVoid => "async System.Threading.Tasks.Task",
                Body.Iterator => "System.Collections.IEnumerator",
                _ => "object",
            };
            file.Append("#line hidden\npublic static ").Append(signature).Append(' ').Append(WrapperMethod).Append("()\n{\n");
            for (int i = 0; i < parts.Statements.Count; i++)
            {
                bool last = i == parts.Statements.Count - 1;
                TextSpan span = last && parts.LastSpan is TextSpan own ? own : parts.Statements[i].FullSpan;
                if (last && candidate)
                {
                    file.Append("#line hidden\nreturn\n");
                    at = Place(file, parts.Source, TextSpan.FromBounds(span.Start, parts.CandidateEnd), label);
                    file.Append("#line hidden\n;\n");
                }
                else
                {
                    Place(file, parts.Source, span, label);
                    if (last && parts.OpenEnd)
                    {
                        file.Append("#line hidden\n;\n");
                    }
                }
            }

            Tail(file, parts, 0, label);
            file.Append("#line hidden\n").Append(body is Body.Value or Body.AsyncValue ? "return null;\n" : "").Append("}\n");
        }

        file.Append("}\n");
        foreach (SyntaxNode type in parts.Types)
        {
            Place(file, parts.Source, type.FullSpan, label);
        }

        Tail(file, parts, 2, label);

        return (CSharpSyntaxTree.ParseText(file.ToString(), parse, path: ""), at);
    }

    private static void Tail(StringBuilder file, Parts parts, int group, string label)
    {
        if (parts.TailAfter == group && parts.Tail.Length > 0)
        {
            Place(file, parts.Source, parts.Tail, label);
        }
    }

    // Writes the code's text at `span` under a `#line` directive for its first line, padded to its
    // column so columns match too; answers where the text starts in the file.
    private static int Place(StringBuilder file, SourceText source, TextSpan span, string label)
    {
        LinePosition start = source.Lines.GetLinePosition(span.Start);
        file.Append("#line ").Append((start.Line + 1).ToString(CultureInfo.InvariantCulture)).Append(" \"").Append(label).Append("\"\n");
        file.Append(' ', start.Character);
        int at = file.Length;
        string text = source.ToString(span);
        file.Append(text);
        if (!text.EndsWith('\n'))
        {
            file.Append('\n');
        }

        return at;
    }

    // ---- errors and the entry ----

    private static List<SnippetError> Errors(IEnumerable<Diagnostic> diagnostics, string name)
    {
        string label = name.Replace('\\', '/');
        var errors = new List<SnippetError>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Diagnostic d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            FileLinePositionSpan mapped = d.Location.GetMappedLineSpan();
            bool own = d.Location.IsInSource && mapped.HasMappedPath && mapped.Path == label;
            var error = new SnippetError(
                own ? mapped.StartLinePosition.Line + 1 : 0,
                own ? mapped.StartLinePosition.Character + 1 : 0,
                d.Id,
                d.GetMessage(CultureInfo.InvariantCulture));
            if (seen.Add(error.ToString()))
            {
                errors.Add(error);
            }
        }

        return [.. errors.OrderBy(e => e.Line).ThenBy(e => e.Column)];
    }

    private static string? FindEntry(CSharpCompilation compilation, SyntaxTree tree, string? entry, out string? entryType, out string entryMethod)
    {
        entryType = null;
        entryMethod = "";
        SemanticModel model = compilation.GetSemanticModel(tree);
        List<INamedTypeSymbol> types = [.. tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Select(t => model.GetDeclaredSymbol(t))
            .OfType<INamedTypeSymbol>()
            .Where(t => t.Name != WrapperType && !t.IsGenericType)];

        string typeName = "";
        string[] methodNames = ["Run", "Main"];
        if (entry != null)
        {
            int dot = entry.LastIndexOf('.');
            if (dot <= 0 || dot == entry.Length - 1)
            {
                return "--entry must be Type.Method";
            }

            typeName = entry[..dot];
            methodNames = [entry[(dot + 1)..]];
            types = [.. types.Where(t => Display(t) == typeName || t.Name == typeName || Display(t).EndsWith("." + typeName, StringComparison.Ordinal))];
            if (types.Count == 0)
            {
                return "no type " + typeName + " in the code";
            }

            if (types.Count > 1)
            {
                return "ambiguous " + typeName + ": " + string.Join(", ", types.Select(Display).Order(StringComparer.Ordinal));
            }
        }

        var found = types
            .SelectMany(t => methodNames.Select(m => (Type: t, Method: m)))
            .Where(c => c.Type.GetMembers(c.Method).OfType<IMethodSymbol>().Any(m => m.IsStatic && m.MethodKind == MethodKind.Ordinary && !m.IsGenericMethod))
            .ToList();
        if (found.Count == 0)
        {
            return entry != null
                ? "no static method " + methodNames[0] + " on " + Display(types[0])
                : "no static Run() or Main(); give --entry Type.Method";
        }

        if (found.Count > 1)
        {
            return "several entries: " + string.Join(", ", found.Select(f => Display(f.Type) + "." + f.Method)) + "; give --entry Type.Method";
        }

        entryType = MetadataName(found[0].Type);
        entryMethod = found[0].Method;
        return null;
    }

    private static string Display(INamedTypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    // `Ns.Outer+Inner`, as Assembly.GetType takes it.
    private static string MetadataName(INamedTypeSymbol type)
    {
        string name = type.MetadataName;
        for (INamedTypeSymbol? outer = type.ContainingType; outer != null; outer = outer.ContainingType)
        {
            name = outer.MetadataName + "+" + name;
            type = outer;
        }

        string space = type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "";
        return space + name;
    }

    /// <summary>
    /// Puts `EvalGuard.Check(seconds);` first in every loop body, on the body's first line so the
    /// code's lines stay as they are.
    /// </summary>
    private sealed class LoopGuard(double seconds) : CSharpSyntaxRewriter
    {
        private readonly StatementSyntax _check = SyntaxFactory.ParseStatement(
            "global::" + GuardType + ".Check(" + seconds.ToString("R", CultureInfo.InvariantCulture) + "D);");

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
            return visited.WithStatement(Guard(visited.Statement));
        }

        private StatementSyntax Guard(StatementSyntax body) =>
            body is BlockSyntax block
                ? block.WithStatements(block.Statements.Insert(0, _check))
                : SyntaxFactory.Block(_check, body.WithoutTrivia()).WithLeadingTrivia(body.GetLeadingTrivia()).WithTrailingTrivia(body.GetTrailingTrivia());
    }
}
