using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UnityGraph.Core.Graph;

namespace UnityGraph.Code;

/// <summary>
/// Turns one syntax tree into nodes and edges (SPEC-code-index, Node rules and Edge rules).
/// Declarations give nodes, `contains`, `inherits`, `implements`, `overrides` and
/// `script_defines`; a walk of the bodies gives `calls`, `uses_type` and `dispatches`, which
/// vendored files skip.
/// </summary>
internal sealed class FileIndexer : CSharpSyntaxWalker
{


    private static readonly HashSet<string> _dispatchers = new(StringComparer.Ordinal)
    {
        "SendMessage", "SendMessageUpwards", "BroadcastMessage", "Invoke", "InvokeRepeating", "CancelInvoke", "IsInvoking",
        "StartCoroutine", "StopCoroutine",
    };

    private static readonly HashSet<string> _dispatcherOwners = new(StringComparer.Ordinal)
    {
        "UnityEngine.Component", "UnityEngine.MonoBehaviour", "UnityEngine.GameObject",
    };

    private static readonly SymbolDisplayFormat _typeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.ExpandNullable);

    private static readonly JsonWriterOptions _jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly CodeWorkspace _workspace;
    private readonly SemanticModel _model;
    private readonly SyntaxTree _tree;
    private readonly string _path;
    private readonly bool _declarationsOnly;
    private readonly string? _scriptGuid;
    private readonly List<Node> _nodes = [];
    private readonly List<Edge> _edges = [];
    private readonly HashSet<(string, EdgeKind, string)> _seen = [];
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<INamedTypeSymbol, Dictionary<ISymbol, List<ISymbol>>> _interfaceMaps = new(SymbolEqualityComparer.Default);

    // The member whose code the walk is in, and the type it belongs to.
    private ISymbol? _source;
    private string? _sourceKey;

    public FileIndexer(CodeWorkspace workspace, SemanticModel model, SyntaxTree tree, string path, bool declarationsOnly, string? scriptGuid)
    {
        _workspace = workspace;
        _model = model;
        _tree = tree;
        _path = path;
        _declarationsOnly = declarationsOnly;
        _scriptGuid = scriptGuid;
    }

    public (IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges) Run()
    {
        Visit(_tree.GetRoot());
        return (_nodes, _edges);
    }

    /// <summary>The graph key of a symbol: its original definition's documentation ID, or null.</summary>
    internal static string? KeyOf(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
        {
            symbol = reduced;
        }

        if (symbol is ITypeSymbol { TypeKind: TypeKind.Error })
        {
            return null;
        }

        string? id = symbol.OriginalDefinition.GetDocumentationCommentId();
        return id is null ? null : NodeKeys.Symbol(id);
    }

    // ---- declarations ------------------------------------------------------------------------

    public override void VisitClassDeclaration(ClassDeclarationSyntax node) => VisitType(node);

    public override void VisitStructDeclaration(StructDeclarationSyntax node) => VisitType(node);

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) => VisitType(node);

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node) => VisitType(node);

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node) => VisitType(node);

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        if (_model.GetDeclaredSymbol(node) is INamedTypeSymbol type && KeyOf(type) is { } key)
        {
            AddTypeNode(type, key, node);
            WalkIn(type, key, () => base.VisitDelegateDeclaration(node));
        }
    }

    private void VisitType(BaseTypeDeclarationSyntax node)
    {
        if (_model.GetDeclaredSymbol(node) is not INamedTypeSymbol type || KeyOf(type) is not { } key)
        {
            return;
        }

        if (IsOwner(type, node))
        {
            AddTypeNode(type, key, node);
        }

        // Whichever part is in the script file: Unity binds the script to the type by file name.
        if (_scriptGuid is not null
            && type is { ContainingType: null, TypeKind: TypeKind.Class }
            && type.Name == Path.GetFileNameWithoutExtension(_path))
        {
            AddEdge(NodeKeys.Asset(_scriptGuid), EdgeKind.ScriptDefines, key, Confidence.Exact, Lines(node).Start);
        }

        if (node.BaseList is { } baseList)
        {
            foreach (BaseTypeSyntax part in baseList.Types)
            {
                if (_model.GetTypeInfo(part.Type).Type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } baseType
                    || baseType.SpecialType == SpecialType.System_Object
                    || KeyOf(baseType) is not { } baseKey)
                {
                    continue;
                }

                EdgeKind kind = baseType.TypeKind == TypeKind.Interface && type.TypeKind != TypeKind.Interface
                    ? EdgeKind.Implements
                    : EdgeKind.Inherits;
                AddEdge(key, kind, baseKey, Confidence.Exact, part);
            }
        }

        // Attributes (e.g. [RequireComponent(typeof(T))]) are code of the type; members walk themselves.
        WalkIn(type, key, () =>
        {
            foreach (AttributeListSyntax attributes in node.AttributeLists)
            {
                Visit(attributes);
            }
        });

        IEnumerable<SyntaxNode> members = node switch
        {
            TypeDeclarationSyntax t => t.Members,
            EnumDeclarationSyntax e => e.Members,
            _ => [],
        };
        foreach (SyntaxNode member in members)
        {
            Visit(member);
        }

        // Record primary constructor parameters and their types.
        if (node is TypeDeclarationSyntax { ParameterList: { } parameters })
        {
            WalkIn(type, key, () => Visit(parameters));
        }
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitDestructorDeclaration(DestructorDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitOperatorDeclaration(OperatorDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitEventDeclaration(EventDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitEnumMemberDeclaration(EnumMemberDeclarationSyntax node) => VisitMember(node, node);

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        foreach (VariableDeclaratorSyntax variable in node.Declaration.Variables)
        {
            VisitMember(variable, node, node.Declaration.Type);
        }
    }

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        foreach (VariableDeclaratorSyntax variable in node.Declaration.Variables)
        {
            VisitMember(variable, node, node.Declaration.Type);
        }
    }

    // `declarator` is what the symbol is declared by, `declaration` spans the lines; for fields
    // the shared type syntax is walked once per declarator.
    private void VisitMember(SyntaxNode declarator, SyntaxNode declaration, TypeSyntax? sharedType = null)
    {
        ISymbol? symbol = _model.GetDeclaredSymbol(declarator);
        if (symbol is null || symbol is IMethodSymbol { PartialImplementationPart: not null } || KeyOf(symbol) is not { } key)
        {
            return;
        }

        // Compile errors can declare a name twice; the first declaration is the node.
        if (!_keys.Add(key))
        {
            return;
        }

        (int start, int end) = Lines(declaration);
        _nodes.Add(new Node(key, NodeKindOf(symbol), NameOf(symbol), _path, start, end, PropsOf(symbol)));
        if (symbol.ContainingType is { } owner && KeyOf(owner) is { } ownerKey)
        {
            AddEdge(ownerKey, EdgeKind.Contains, key, Confidence.Exact, start);
        }

        foreach (ISymbol overridden in Overridden(symbol))
        {
            if (KeyOf(overridden) is { } overriddenKey)
            {
                AddEdge(key, EdgeKind.Overrides, overriddenKey, Confidence.Exact, start);
            }
        }

        WalkIn(symbol, key, () =>
        {
            if (sharedType is not null)
            {
                Visit(sharedType);
                Visit(declarator);
            }
            else
            {
                DefaultVisit(declaration);
            }
        });
    }

    private void AddTypeNode(INamedTypeSymbol type, string key, SyntaxNode node)
    {
        if (!_keys.Add(key))
        {
            return;
        }

        (int start, int end) = Lines(node);
        _nodes.Add(new Node(key, NodeKind.Type, type.Name, _path, start, end, PropsOf(type)));
        if (type.ContainingType is { } outer && KeyOf(outer) is { } outerKey)
        {
            AddEdge(outerKey, EdgeKind.Contains, key, Confidence.Exact, start);
        }
    }

    // A partial type's node belongs to the part in the file named like the type (`LabStage.cs`
    // over `LabStage.Debug.cs`), else to the ordinally first file; then the first position.
    private static bool IsOwner(INamedTypeSymbol type, SyntaxNode node)
    {
        SyntaxReference first = type.DeclaringSyntaxReferences
            .OrderBy(r => Path.GetFileNameWithoutExtension(r.SyntaxTree.FilePath) == type.Name ? 0 : 1)
            .ThenBy(r => r.SyntaxTree.FilePath, StringComparer.Ordinal)
            .ThenBy(r => r.Span.Start)
            .First();
        return first.SyntaxTree == node.SyntaxTree && first.Span == node.Span;
    }

    private IEnumerable<ISymbol> Overridden(ISymbol symbol)
    {
        ISymbol? overridden = symbol switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            IEventSymbol e => e.OverriddenEvent,
            _ => null,
        };
        if (overridden is not null)
        {
            yield return overridden;
        }

        if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol && symbol.ContainingType is { } type
            && InterfaceMap(type).TryGetValue(symbol, out List<ISymbol>? implemented))
        {
            foreach (ISymbol member in implemented)
            {
                yield return member;
            }
        }
    }

    // Implementing member → the interface members it implements, per type.
    private Dictionary<ISymbol, List<ISymbol>> InterfaceMap(INamedTypeSymbol type)
    {
        if (_interfaceMaps.TryGetValue(type, out var map))
        {
            return map;
        }

        map = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);
        if (type.TypeKind != TypeKind.Interface)
        {
            foreach (INamedTypeSymbol iface in type.AllInterfaces)
            {
                foreach (ISymbol member in iface.GetMembers())
                {
                    if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol
                        && type.FindImplementationForInterfaceMember(member) is { } impl
                        && SymbolEqualityComparer.Default.Equals(impl.ContainingType, type))
                    {
                        if (!map.TryGetValue(impl, out List<ISymbol>? list))
                        {
                            map[impl] = list = [];
                        }

                        list.Add(member);
                    }
                }
            }
        }

        _interfaceMaps[type] = map;
        return map;
    }

    // ---- code ----------------------------------------------------------------------------------

    private void WalkIn(ISymbol source, string key, Action walk)
    {
        if (_declarationsOnly)
        {
            return;
        }

        (ISymbol? outerSource, string? outerKey) = (_source, _sourceKey);
        (_source, _sourceKey) = (source, key);
        walk();
        (_source, _sourceKey) = (outerSource, outerKey);
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitIdentifierName(node);
    }

    public override void VisitGenericName(GenericNameSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitGenericName(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitConstructorInitializer(ConstructorInitializerSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitConstructorInitializer(node);
    }

    public override void VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitBinaryExpression(node);
    }

    public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitPrefixUnaryExpression(node);
    }

    public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitPostfixUnaryExpression(node);
    }

    public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            Reference(node, _model.GetSymbolInfo(node).Symbol);
        }

        base.VisitAssignmentExpression(node);
    }

    public override void VisitCastExpression(CastExpressionSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitCastExpression(node);
    }

    public override void VisitElementAccessExpression(ElementAccessExpressionSyntax node)
    {
        Reference(node, _model.GetSymbolInfo(node).Symbol);
        base.VisitElementAccessExpression(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        if (IsNameof(node))
        {
            // `nameof(Spin)` names a method without calling it; dispatch handles it as an argument.
            return;
        }

        if (_source is not null && _model.GetSymbolInfo(node).Symbol is IMethodSymbol method)
        {
            Dispatch(node, method);
        }

        base.VisitInvocationExpression(node);
    }

    // Local functions and lambdas have no node: their code belongs to the enclosing member.
    // Nested type declarations inside members don't exist in C#, so nothing else resets the source.
    private void Reference(SyntaxNode at, ISymbol? target)
    {
        if (_source is null || _sourceKey is null || target is null)
        {
            return;
        }

        EdgeKind kind;
        switch (target)
        {
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }:
                return;
            case IMethodSymbol { MethodKind: MethodKind.BuiltinOperator }:
                return;

            // A default constructor has no node; `new T()` still gives `uses_type T`.
            case IMethodSymbol { IsImplicitlyDeclared: true }:
                return;
            case IMethodSymbol or IPropertySymbol or IEventSymbol:
                kind = EdgeKind.Calls;
                break;
            case INamedTypeSymbol type:
                if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, _source.ContainingType?.OriginalDefinition)
                    || SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, _source.OriginalDefinition))
                {
                    return;
                }

                kind = EdgeKind.UsesType;
                break;
            default:
                return;
        }

        ISymbol definition = target is IMethodSymbol { ReducedFrom: { } reduced } ? reduced : target.OriginalDefinition;
        if (_workspace.IsIndexedSource(definition) && KeyOf(definition) is { } key)
        {
            AddEdge(_sourceKey, kind, key, Confidence.Exact, at);
        }
    }

    private bool IsNameof(InvocationExpressionSyntax node) =>
        node.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && _model.GetSymbolInfo(node).Symbol is null;

    // A string argument to SendMessage, Invoke, StartCoroutine and the like names the method called.
    private void Dispatch(InvocationExpressionSyntax node, IMethodSymbol method)
    {
        if (!_dispatchers.Contains(method.Name)
            || method.ContainingType is null
            || !_dispatcherOwners.Contains(UnityRules.FullName(method.ContainingType))
            || node.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } argument)
        {
            return;
        }

        List<IMethodSymbol> targets;
        bool exact;
        if (argument is InvocationExpressionSyntax nameofCall && IsNameof(nameofCall))
        {
            if (nameofCall.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } named)
            {
                return;
            }

            SymbolInfo info = _model.GetSymbolInfo(named);
            targets = (info.Symbol is null ? info.CandidateSymbols : [info.Symbol]).OfType<IMethodSymbol>().ToList();
            exact = true;
        }
        else if (_model.GetConstantValue(argument) is { HasValue: true, Value: string name } && name.Length > 0)
        {
            if (method.Name.Contains("Message", StringComparison.Ordinal))
            {
                targets = _workspace.MessageTargets(_model.Compilation, name)
                    .OrderBy(m => KeyOf(m), StringComparer.Ordinal)
                    .ToList();
                exact = false;
            }
            else
            {
                ITypeSymbol? receiver = node.Expression is MemberAccessExpressionSyntax access
                    ? _model.GetTypeInfo(access.Expression).Type
                    : _source?.ContainingType;
                targets = MethodsNamed(receiver, name);
                exact = targets.Count == 1;
            }
        }
        else
        {
            return;
        }

        foreach (IMethodSymbol target in targets)
        {
            if (_workspace.IsIndexedSource(target.OriginalDefinition) && KeyOf(target) is { } key)
            {
                AddEdge(_sourceKey!, EdgeKind.Dispatches, key, exact ? Confidence.Exact : Confidence.Heuristic, argument);
            }
        }
    }

    // Methods of that name on the type and its bases, declared in indexed code; an override
    // hides the method it overrides, so `Open` on a subclass is one target, not two.
    private List<IMethodSymbol> MethodsNamed(ITypeSymbol? type, string name)
    {
        var found = new List<IMethodSymbol>();
        for (ITypeSymbol? t = type; t is not null; t = t.BaseType)
        {
            found.AddRange(t.GetMembers(name).OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Ordinary && _workspace.IsIndexedSource(m.OriginalDefinition)));
        }

        var hidden = new HashSet<ISymbol>(found.Select(m => m.OverriddenMethod?.OriginalDefinition).OfType<ISymbol>(), SymbolEqualityComparer.Default);
        return found.Where(m => !hidden.Contains(m.OriginalDefinition)).ToList();
    }

    // ---- rows ----------------------------------------------------------------------------------

    private void AddEdge(string src, EdgeKind kind, string dst, Confidence confidence, SyntaxNode at, string? props = null) =>
        AddEdge(src, kind, dst, confidence, Lines(at).Start, props);

    // One edge per (src, kind, dst) per file: the walk runs in source order, so the first is the first line.
    private void AddEdge(string src, EdgeKind kind, string dst, Confidence confidence, int line, string? props = null)
    {
        if (_seen.Add((src, kind, dst)))
        {
            _edges.Add(new Edge(src, kind, dst, confidence, _path, line, props));
        }
    }

    // A declaration starts after its attribute lists, on the line that declares it (B1e:
    // `[Serializable]` above a class put the class on the attribute's line).
    private (int Start, int End) Lines(SyntaxNode node)
    {
        int start = node is MemberDeclarationSyntax { AttributeLists: { Count: > 0 } lists }
            ? lists.Last().GetLastToken().GetNextToken().SpanStart
            : node.SpanStart;
        FileLinePositionSpan span = _tree.GetLineSpan(TextSpan.FromBounds(start, node.Span.End));
        return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    private static NodeKind NodeKindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol => NodeKind.Type,
        IMethodSymbol => NodeKind.Method,
        IFieldSymbol => NodeKind.Field,
        IPropertySymbol => NodeKind.Property,
        IEventSymbol => NodeKind.Event,
        _ => throw new ArgumentException("not a member: " + symbol.Kind, nameof(symbol)),
    };

    private static string NameOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } m => m.ContainingType.Name,
        IPropertySymbol { IsIndexer: true } => "this[]",

        // Explicit interface implementations are named `Game.ILabDebug.Trace`.
        _ => symbol.Name.LastIndexOf('.') is > 0 and int dot ? symbol.Name[(dot + 1)..] : symbol.Name,
    };

    // ---- props ---------------------------------------------------------------------------------

    private static string? PropsOf(ISymbol symbol)
    {
        var props = new Props();
        switch (symbol)
        {
            case INamedTypeSymbol type:
                props.Text("subkind", type.TypeKind switch
                {
                    TypeKind.Class when type.IsRecord => "record",
                    TypeKind.Struct when type.IsRecord => "record_struct",
                    TypeKind.Class => "class",
                    TypeKind.Struct => "struct",
                    TypeKind.Interface => "interface",
                    TypeKind.Enum => "enum",
                    TypeKind.Delegate => "delegate",
                    _ => "other",
                });
                props.Text("access", Access(type.DeclaredAccessibility));
                props.Flag("static", type.IsStatic);
                props.Flag("abstract", type.IsAbstract && type.TypeKind == TypeKind.Class);
                props.Flag("sealed", type.IsSealed && type.TypeKind == TypeKind.Class && !type.IsStatic);
                if (type.Arity > 0)
                {
                    props.Number("generic", type.Arity);
                }

                string? unity = UnityRules.KindOf(type) ?? (type.IsSerializable && type.TypeKind is TypeKind.Class or TypeKind.Struct ? "serializable" : null);
                props.Text("unity", unity);
                props.Text("entry", UnityRules.EntryOf(type));
                break;
            case IMethodSymbol method:
                props.Text("sig", Signature(method));
                props.Text("access", Access(method.DeclaredAccessibility));
                props.Text("subkind", method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ? "constructor" : null);
                props.Flag("static", method.IsStatic);
                props.Flag("virtual", method.IsVirtual);
                props.Flag("abstract", method.IsAbstract && method.ContainingType.TypeKind != TypeKind.Interface);
                props.Flag("override", method.IsOverride);
                props.Flag("async", method.IsAsync);
                props.Flag("message", UnityRules.IsMessage(method));
                props.Text("entry", UnityRules.EntryOf(method));
                break;
            case IFieldSymbol field:
                props.Text("type", field.Type.ToDisplayString(_typeFormat));
                props.Text("access", Access(field.DeclaredAccessibility));
                props.Flag("static", field.IsStatic && !field.IsConst);
                props.Flag("const", field.IsConst);
                props.Flag("readonly", field.IsReadOnly);
                props.Flag("serialized", UnityRules.IsSerialized(field, out bool reference));
                props.Flag("serialize_reference", reference);
                props.List("former", UnityRules.FormerNames(field));
                break;
            case IPropertySymbol property:
                props.Text("type", property.Type.ToDisplayString(_typeFormat));
                props.Text("access", Access(property.DeclaredAccessibility));
                props.Flag("static", property.IsStatic);
                if (BackingField(property) is { } backing)
                {
                    props.Flag("serialized", UnityRules.IsSerialized(backing, out bool backingReference));
                    props.Flag("serialize_reference", backingReference);
                    props.List("former", UnityRules.FormerNames(backing));
                }

                break;
            case IEventSymbol @event:
                props.Text("type", @event.Type.ToDisplayString(_typeFormat));
                props.Text("access", Access(@event.DeclaredAccessibility));
                props.Flag("static", @event.IsStatic);
                break;
        }

        return props.ToJson();
    }

    private static IFieldSymbol? BackingField(IPropertySymbol property) =>
        property.ContainingType.GetMembers()
            .OfType<IFieldSymbol>()
            .FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.AssociatedSymbol, property));

    private static string Signature(IMethodSymbol method)
    {
        var sig = new StringBuilder(NameOf(method));
        if (method.Arity > 0)
        {
            sig.Append('<').AppendJoin(", ", method.TypeParameters.Select(t => t.Name)).Append('>');
        }

        sig.Append('(');
        sig.AppendJoin(", ", method.Parameters.Select(p => p.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => p.IsParams ? "params " : "",
        } + p.Type.ToDisplayString(_typeFormat)));
        return sig.Append(')').ToString();
    }


    private static string Access(Accessibility access) => access switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "private",
    };

    /// <summary>A small JSON object, keys in the order added; flags and empty values are left out.</summary>
    private sealed class Props
    {
        private readonly List<(string Key, object Value)> _items = [];

        public void Text(string key, string? value)
        {
            if (value is not null)
            {
                _items.Add((key, value));
            }
        }

        public void Flag(string key, bool value)
        {
            if (value)
            {
                _items.Add((key, true));
            }
        }

        public void Number(string key, int value) => _items.Add((key, value));

        public void List(string key, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
            {
                _items.Add((key, values));
            }
        }

        public string? ToJson()
        {
            if (_items.Count == 0)
            {
                return null;
            }

            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, _jsonOptions))
            {
                json.WriteStartObject();
                foreach ((string key, object value) in _items)
                {
                    switch (value)
                    {
                        case string s:
                            json.WriteString(key, s);
                            break;
                        case bool b:
                            json.WriteBoolean(key, b);
                            break;
                        case int n:
                            json.WriteNumber(key, n);
                            break;
                        case IReadOnlyList<string> list:
                            json.WriteStartArray(key);
                            foreach (string item in list)
                            {
                                json.WriteStringValue(item);
                            }

                            json.WriteEndArray();
                            break;
                    }
                }

                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
