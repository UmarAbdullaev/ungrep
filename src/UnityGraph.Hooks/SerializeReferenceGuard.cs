using System.Text.RegularExpressions;
using UnityGraph.Query;

namespace UnityGraph.Hooks;

/// <summary>
/// Case 2 (`serialize-reference`): Unity stores a `[SerializeReference]` value by assembly,
/// namespace and class name, so renaming its type, changing the namespace (an edit) or the
/// assembly (a move into another `.asmdef`) orphans saved instances, unless the new text has
/// `[MovedFrom(...)]`. Only concrete types count: an interface or abstract class is never the
/// stored type of an instance.
/// </summary>
internal sealed class SerializeReferenceGuard : IGuard
{
    private static readonly Regex _movedFrom = new(@"\bMovedFrom(Attribute)?\s*\(", RegexOptions.CultureInvariant);

    public string Case => "serialize-reference";

    public IEnumerable<GuardNote> Check(GuardContext context) =>
        context.Input.ToolName is "Bash" or "PowerShell" ? Moves(context) : Edits(context);

    private IEnumerable<GuardNote> Edits(GuardContext context)
    {
        if (EditCall.From(context.Input) is not { } call || call.Edits.Count == 0 && call.Content is null
            || context.OwnScript(call.FilePath) is not { } path || context.Graph is not { } graph || !context.IsFresh(path))
        {
            yield break;
        }

        string text = EditCall.Lf(File.ReadAllText(context.FullPath(path)));
        if (call.Apply(text) is not { } after || _movedFrom.IsMatch(after))
        {
            yield break;
        }

        // Write: the old text is the whole file.
        IEnumerable<Replacement> edits = call.Content is { } content ? [new Replacement(text, content, false)] : call.Edits;
        foreach (DeclaredType type in graph.TypesIn(path).Where(t => t.Concrete))
        {
            bool changed = edits.Any(e =>
                Declaration(type.Name).IsMatch(e.Old) && !EditCall.Identifier(type.Name).IsMatch(e.New)
                || type.Namespace.Length > 0 && Namespace(type.Namespace).IsMatch(e.Old) && !Namespace(type.Namespace).IsMatch(e.New));
            if (changed && graph.SerializeReferenceUsers(type.Key) is { Count: > 0 } users)
            {
                yield return Note(type, users, null, ScriptAssembly.Of(context, ScriptAssembly.Parent(path)));
            }
        }
    }

    private IEnumerable<GuardNote> Moves(GuardContext context)
    {
        foreach (ShellOp op in context.ShellOps().Where(o => o.Kind == FileOpKind.Move && GuardContext.InAssets(o.Destination)))
        {
            string destination = op.Destination!;
            bool intoFolder = op.Sources.Count > 1 || Directory.Exists(context.FullPath(destination));
            foreach (string source in op.Sources.OfType<string>().Where(s => GuardContext.InAssets(s) && s.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                if (context.Graph is not { } graph || !File.Exists(context.FullPath(source))
                    || _movedFrom.IsMatch(File.ReadAllText(context.FullPath(source)))
                    || ScriptAssembly.Of(context, ScriptAssembly.Parent(source)) is not { } from
                    || ScriptAssembly.Of(context, intoFolder ? destination : ScriptAssembly.Parent(destination)) is not { } to
                    || from == to)
                {
                    continue;
                }

                foreach (DeclaredType type in graph.TypesIn(source).Where(t => t.Concrete))
                {
                    if (graph.SerializeReferenceUsers(type.Key) is { Count: > 0 } users)
                    {
                        yield return Note(type, users, to, from);
                    }
                }
            }
        }
    }

    // movedTo: the destination assembly of a move; null for an edit.
    private GuardNote Note(DeclaredType type, IReadOnlyList<(string Field, IReadOnlyList<string> Holders)> users, string? movedTo, string? assembly)
    {
        string[] files = [.. users.SelectMany(u => u.Holders).Distinct(StringComparer.Ordinal)];
        string fields = users[0].Field + (users.Count > 1 ? $" (+{users.Count - 1} more)" : "");
        string hold = $"up to {GuardOutput.Assets(files)} may hold one (ug impact {type.Name})";
        string change = movedTo is null
            ? $"  Renaming it or changing its namespace or assembly orphans saved instances: {hold}."
            : $"  Moving the file to {movedTo} changes its assembly and orphans saved instances: {hold}.";
        var from = new List<string> { "true" };
        if (movedTo is null && type.Namespace.Length > 0)
        {
            from.Add($"sourceNamespace: \"{type.Namespace}\"");
        }

        if (assembly is not null)
        {
            from.Add($"sourceAssembly: \"{assembly}\"");
        }

        if (movedTo is null)
        {
            from.Add($"sourceClassName: \"{type.Name}\"");
        }

        return new GuardNote(Case, type.Key + (movedTo is null ? "" : " " + movedTo),
            $"unitygraph: {type.Qualified} is stored by name in [SerializeReference] fields ({fields}).\n"
            + change + "\n"
            + $"  Add [MovedFrom({string.Join(", ", from)})] to carry them over.");
    }

    private static Regex Declaration(string name) =>
        new(@"(?<!\w)(class|struct|interface|record)(\s+(class|struct))?\s+" + Regex.Escape(name) + @"(?!\w)", RegexOptions.CultureInvariant);

    private static Regex Namespace(string name) =>
        new(@"(?<![\w.])namespace\s+" + Regex.Escape(name) + @"(?![\w.])", RegexOptions.CultureInvariant);
}
