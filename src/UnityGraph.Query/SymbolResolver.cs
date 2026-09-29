using UnityGraph.Core.Graph;

namespace UnityGraph.Query;

/// <summary>What a symbol argument named: the nodes, and whether they are one symbol.</summary>
public sealed record Resolution(IReadOnlyList<Node> Nodes, bool Ambiguous)
{
    public bool Found => Nodes.Count > 0 && !Ambiguous;
}

/// <summary>
/// Turns `&lt;symbol&gt;` text into nodes (SPEC-query, Resolving): a `cs:` key, or a dotted name that
/// a node's qualified name ends with, optionally with a parameter list matched against `sig`.
/// Overloads of one method named without parameters are one symbol.
/// </summary>
public static class SymbolResolver
{
    private const int Candidates = 500;

    public static Resolution Resolve(GraphStore store, string text)
    {
        text = text.Trim();
        if (text.StartsWith("cs:", StringComparison.Ordinal))
        {
            return store.GetNode(text) is { } node ? new Resolution([node], false) : new Resolution([], false);
        }

        int paren = text.IndexOf('(', StringComparison.Ordinal);
        string name = (paren < 0 ? text : text[..paren]).Trim();
        string? parameters = paren < 0 ? null : Squeeze(text[paren..]);
        string last = name[(name.LastIndexOf('.') + 1)..];
        if (last.Length == 0)
        {
            return new Resolution([], false);
        }

        List<Node> named = store.FindByName(last, NodeKindSet.All, Candidates).Items
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

        bool oneSymbol = matches.Count <= 1
            || (matches.All(n => n.Kind == NodeKind.Method)
                && matches.Select(n => Names.Qualified(n.Key)).Distinct(StringComparer.Ordinal).Count() == 1);
        return new Resolution(matches, !oneSymbol);
    }

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
