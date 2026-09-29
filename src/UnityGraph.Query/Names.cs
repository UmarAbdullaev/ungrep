using System.Text;
using System.Text.Json;
using UnityGraph.Core.Graph;

namespace UnityGraph.Query;

/// <summary>How nodes read in answers: qualified names, kinds, locations and props.</summary>
internal static class Names
{
    /// <summary>
    /// `cs:M:Game.Core.Pool`1.Release(`0)` → `Game.Core.Pool.Release`: the doc ID without its
    /// prefix, parameters and generic arity marks. Other keys come back unchanged.
    /// </summary>
    public static string Qualified(string key)
    {
        if (!key.StartsWith("cs:", StringComparison.Ordinal) || key.Length < 5 || key[4] != ':')
        {
            return key;
        }

        string id = key[5..];
        int paren = id.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            id = id[..paren];
        }

        var text = new StringBuilder(id.Length);
        for (int i = 0; i < id.Length; i++)
        {
            if (id[i] == '`')
            {
                while (i + 1 < id.Length && (id[i + 1] == '`' || char.IsAsciiDigit(id[i + 1])))
                {
                    i++;
                }

                continue;
            }

            text.Append(id[i]);
        }

        return text.ToString();
    }

    /// <summary>A method as `Qualified.Type.Name(int, string)` from its `sig`; anything else as <see cref="Qualified"/>.</summary>
    public static string Display(Node node)
    {
        string qualified = Qualified(node.Key);
        if (node.Kind == NodeKind.Method && Prop(node, "sig") is { } sig)
        {
            int dot = qualified.LastIndexOf('.');
            return (dot < 0 ? "" : qualified[..(dot + 1)]) + sig;
        }

        return qualified;
    }

    /// <summary>The subkind for types (`class`, `struct`, ...), the node kind otherwise.</summary>
    public static string Kind(Node node) =>
        node.Kind == NodeKind.Type && Prop(node, "subkind") is { } subkind ? subkind : KindNames.Of(node.Kind);

    public static string Location(Node node) =>
        node.LineEnd > node.LineStart ? $"{node.File}:{node.LineStart}-{node.LineEnd}" : $"{node.File}:{node.LineStart}";

    public static string Location(Edge edge) => $"{edge.File}:{edge.Line}";

    public static string Confidence(Confidence confidence) => confidence == Core.Graph.Confidence.Exact ? "[exact]" : "[heuristic]";

    public static string? Prop(Node node, string name) =>
        Props(node) is { } props && props.TryGetValue(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public static bool Flag(Node node, string name) => Prop(node, name) == "true";

    public static IReadOnlyList<string> List(Node node, string name) =>
        Props(node) is { } props && props.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(v => v.GetString() ?? "")]
            : [];

    /// <summary>Joins at most <paramref name="max"/> items, then `(+N)`; `(none)` when empty.</summary>
    public static string Join(IReadOnlyList<string> items, int max = 8) =>
        items.Count == 0 ? "(none)"
        : items.Count <= max ? string.Join(", ", items)
        : string.Join(", ", items.Take(max)) + $" (+{items.Count - max})";

    /// <summary>A command argument as a shell would need it: bare when safe, else single-quoted.</summary>
    public static string Quote(string text) =>
        text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')
            ? text
            : "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static Dictionary<string, JsonElement>? Props(Node node)
    {
        if (node.Props is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(node.Props);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
