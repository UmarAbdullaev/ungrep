using UnityGraph.Core.Graph;

namespace UnityGraph.Query;

/// <summary>What a symbol argument named: the nodes, and whether they are one symbol.</summary>
public sealed record Resolution(IReadOnlyList<Node> Nodes, bool Ambiguous)
{
    public bool Found => Nodes.Count > 0 && !Ambiguous;
}

/// <summary>
/// Turns `&lt;symbol&gt;` text into nodes (SPEC-query, Resolving): a `cs:`, `a:` or `o:` key, a GUID,
/// a component as answers show it (`Run &gt; RunDirector`), an asset path or file name, or a dotted name that a node's qualified name ends with,
/// optionally with a parameter list matched against `sig`; failing those, a game object or prefab
/// instance by name or hierarchy path (`Level/Spawn`).
/// Overloads of one method named without parameters are one symbol.
/// </summary>
public static class SymbolResolver
{
    private const int Candidates = 500;
    private static readonly NodeKindSet _sceneObjects = NodeKindSet.Of(NodeKind.GameObject, NodeKind.PrefabInstance);

    public static Resolution Resolve(GraphStore store, string text, bool all = false)
    {
        text = text.Trim();
        if (text.StartsWith("cs:", StringComparison.Ordinal) || text.StartsWith("a:", StringComparison.Ordinal) || text.StartsWith("o:", StringComparison.Ordinal))
        {
            return One(store.GetNode(text));
        }

        if (text.Length == 32 && text.All(char.IsAsciiHexDigitLower))
        {
            return One(store.GetNode(NodeKeys.Asset(text)));
        }

        if (text.Contains(" > ", StringComparison.Ordinal) && Components(store, text, all) is { Nodes.Count: > 0 } components)
        {
            return components;
        }

        if (text.Contains('/', StringComparison.Ordinal))
        {
            string meta = (text.EndsWith(".meta", StringComparison.Ordinal) ? text : text + ".meta").TrimStart('/');
            Node? asset = store.KeysInFile(meta).Select(store.GetNode).FirstOrDefault(n => n?.Kind == NodeKind.Asset);
            return asset is null ? Objects(store, text.Trim('/'), all) : One(asset);
        }

        if (text.Contains('.', StringComparison.Ordinal) && !text.Contains('(', StringComparison.Ordinal))
        {
            List<Node> assets = store.FindByName(text, NodeKindSet.Of(NodeKind.Asset), all ? int.MaxValue - 1 : Candidates).Items.ToList();
            List<Node> files = assets.Where(n => n.Name == text).ToList();
            files = files.Count > 0 ? files : assets.Where(n => string.Equals(n.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (files.Count > 0)
            {
                return new Resolution([.. files.OrderBy(n => n.File, StringComparer.Ordinal)], files.Count > 1);
            }
        }

        int paren = text.IndexOf('(', StringComparison.Ordinal);
        string name = (paren < 0 ? text : text[..paren]).Trim();
        string? parameters = paren < 0 ? null : Squeeze(text[paren..]);
        string last = name[(name.LastIndexOf('.') + 1)..];
        if (last.Length == 0)
        {
            return Objects(store, text, all);
        }

        List<Node> named = store.FindByName(last, NodeKindSet.All, all ? int.MaxValue - 1 : Candidates).Items
            .Where(n => string.Equals(n.Name, last, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (parameters is not null)
        {
            named = named.Where(n => Names.Prop(n, "sig") is { } sig && sig.Contains('(', StringComparison.Ordinal)
                && Squeeze(sig[sig.IndexOf('(', StringComparison.Ordinal)..]) == parameters).ToList();
        }

        List<Node> matches = Matching(named, name, StringComparison.Ordinal);
        if (matches.Count == 0)
        {
            matches = Matching(named, name, StringComparison.OrdinalIgnoreCase);
        }

        if (matches.Count == 0 && parameters is null)
        {
            return Objects(store, text, all);
        }

        bool oneSymbol = matches.Count <= 1
            || (matches.All(n => n.Kind == NodeKind.Method)
                && matches.Select(n => Names.Qualified(n.Key)).Distinct(StringComparer.Ordinal).Count() == 1);
        return new Resolution(matches, !oneSymbol);
    }

    // Game objects and prefab instances named `text`, or whose hierarchy path ends with it on a `/`.
    private static Resolution Objects(GraphStore store, string text, bool all)
    {
        string name = text[(text.LastIndexOf('/') + 1)..];
        if (name.Length == 0)
        {
            return new Resolution([], false);
        }

        List<Node> named = store.FindByName(name, _sceneObjects, all ? int.MaxValue - 1 : Candidates).Items.ToList();
        foreach (StringComparison comparison in (StringComparison[])[StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase])
        {
            List<Node> matches = named
                .Where(n => string.Equals(n.Name, name, comparison))
                .Where(n => ObjectPath(store, n, 0) is { } path && (path.Equals(text, comparison) || path.EndsWith("/" + text, comparison)))
                .OrderBy(n => n.File, StringComparer.Ordinal)
                .ThenBy(n => n.Key, StringComparer.Ordinal)
                .ToList();
            if (matches.Count > 0)
            {
                return new Resolution(matches, matches.Count > 1);
            }
        }

        return new Resolution([], false);
    }

    // A component as answers show it: `Run > RunDirector`, or as `explain` lists where a script is
    // on, after its file: `Arena_Greybox.unity > Run > RunDirector`.
    private static Resolution Components(GraphStore store, string text, bool all)
    {
        string[] parts = text.Split(" > ", StringSplitOptions.TrimEntries);
        if (parts.Length > 3 || parts.Any(p => p.Length == 0))
        {
            return new Resolution([], false);
        }

        string? file = parts.Length == 3 ? parts[0] : null;
        List<Node> objects = [.. Objects(store, parts[^2].Trim('/'), all).Nodes
            .Where(o => file is null || o.File == file || Path.GetFileName(o.File) == file)];
        List<Node> components = [.. objects
            .SelectMany(o => store.EdgesFrom(o.Key, EdgeKindSet.Of(EdgeKind.Contains), all ? int.MaxValue - 1 : Candidates).Items)
            .Select(h => store.GetNode(h.Edge.Dst))
            .OfType<Node>()
            .Where(n => n.Kind == NodeKind.Component)];
        foreach (StringComparison comparison in (StringComparison[])[StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase])
        {
            List<Node> matches = [.. components
                .Where(n => string.Equals(n.Name, parts[^1], comparison))
                .OrderBy(n => n.File, StringComparer.Ordinal)
                .ThenBy(n => n.Key, StringComparer.Ordinal)];
            if (matches.Count > 0)
            {
                return new Resolution(matches, matches.Count > 1);
            }
        }

        return new Resolution([], false);
    }

    // A game object's indexed `path`; a prefab instance's parent path plus its name.
    private static string ObjectPath(GraphStore store, Node node, int depth)
    {
        if (node.Kind == NodeKind.GameObject)
        {
            return Names.Prop(node, "path") ?? node.Name;
        }

        string? parent = store.EdgesFrom(node.Key, EdgeKindSet.Of(EdgeKind.ChildOf), 1).Items.FirstOrDefault()?.Edge.Dst;
        return depth < 100 && parent is not null && store.GetNode(parent) is { } up
            ? ObjectPath(store, up, depth + 1) + "/" + node.Name
            : node.Name;
    }

    private static Resolution One(Node? node) => node is null ? new Resolution([], false) : new Resolution([node], false);

    private static List<Node> Matching(List<Node> nodes, string name, StringComparison comparison) =>
        nodes.Where(n =>
            {
                string qualified = Names.Qualified(n.Key);
                return qualified.Equals(name, comparison)
                    || (qualified.EndsWith(name, comparison) && qualified[qualified.Length - name.Length - 1] == '.');
            })
            .OrderBy(n => n.Key, StringComparer.Ordinal)
            .ToList();

    private static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
}
