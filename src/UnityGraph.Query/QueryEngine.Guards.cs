using UnityGraph.Core.Graph;

namespace UnityGraph.Query;

/// <summary>
/// A serialized field or auto-property declared in a file (SPEC-guards, case 1): its key and C#
/// name (a property's data is saved under `&lt;Name&gt;k__BackingField`), the declaring type as key,
/// name and qualified name, and the declaration's lines.
/// </summary>
public sealed record SerializedMember(string Key, string Name, bool IsProperty, string Owner, string OwnerName, string OwnerQualified, int LineStart, int LineEnd);

/// <summary>
/// A top-level type declared in a file, with its namespace (empty for the global one); concrete
/// when it is a class, struct or record that is neither abstract nor static (a type whose instances exist).
/// </summary>
public sealed record DeclaredType(string Key, string Name, string Namespace, string Qualified, bool Concrete);

public sealed partial class QueryEngine
{
    /// <summary>
    /// Asset files holding serialized data of a type: scenes, prefabs and assets with a component or
    /// ScriptableObject of the type or a subclass, those holding it as serialized data of another
    /// type, and those whose `[SerializeReference]` fields may hold an instance (the holders `ug
    /// impact` lists), ordered.
    /// </summary>
    /// <param name="typeKey">The `cs:T:...` key of the type.</param>
    public IReadOnlyList<string> DataHolders(string typeKey)
    {
        IEnumerable<string> files = HoldersOf(TypeClosure(typeKey)).Select(h => h.File)
            .Concat(ViaFields(typeKey, ViaDepth, [typeKey]).SelectMany(v => v.Holders).Select(h => h.File));
        if (Node(typeKey) is { } type)
        {
            files = files.Concat(SerializeReferenceHolders(type).SelectMany(v => v.Holders).Select(h => h.File));
        }

        return [.. files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// `[SerializeReference]` fields typed with the type or one of its bases or interfaces, as their
    /// qualified names with the asset files that hold data of the field's owner (an instance of the
    /// type may be stored in any of them), ordered by field. Fields whose owner has no holders are left out.
    /// </summary>
    public IReadOnlyList<(string Field, IReadOnlyList<string> Holders)> SerializeReferenceUsers(string typeKey)
    {
        var result = new List<(string, IReadOnlyList<string>)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string ancestor in new[] { typeKey }.Concat(Ancestors(typeKey)))
        {
            foreach (Edge use in In(ancestor).Where(e => e.Kind == EdgeKind.UsesType).DistinctBy(e => e.Src))
            {
                if (Node(use.Src) is not { Kind: NodeKind.Field } field || !Names.Flag(field, "serialize_reference")
                    || In(field.Key).FirstOrDefault(e => e.Kind == EdgeKind.Contains)?.Src is not { } owner || !seen.Add(field.Key))
                {
                    continue;
                }

                IReadOnlyList<string> holders = DataHolders(owner);
                if (holders.Count > 0)
                {
                    result.Add((Names.Qualified(field.Key), holders));
                }
            }
        }

        return [.. result.OrderBy(r => r.Item1, StringComparer.Ordinal)];
    }

    /// <summary>Files with a reference into the asset `a:guid` or one of its objects, without the asset's own files, ordered.</summary>
    public IReadOnlyList<string> AssetReferrers(string guid, string assetFile) =>
        [.. _store.EdgesIntoAsset(guid, EdgeKindSet.All, EdgeLimit).Items.Select(h => h.Edge.File)
            .Where(f => f != assetFile && f != assetFile + ".meta").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>The GUID of the asset whose `.meta` file is <paramref name="metaFile"/>, or null when the graph has no asset for it.</summary>
    public string? AssetGuid(string metaFile) =>
        _store.KeysInFile(metaFile).FirstOrDefault(k => k.StartsWith("a:", StringComparison.Ordinal))?[2..];

    /// <summary>The serialized fields and properties declared in a script, in line order.</summary>
    public IReadOnlyList<SerializedMember> SerializedMembers(string file)
    {
        var members = new List<SerializedMember>();
        foreach (string key in _store.KeysInFile(file))
        {
            if (Node(key) is not { Kind: NodeKind.Field or NodeKind.Property } member || !Names.Flag(member, "serialized")
                || In(key).FirstOrDefault(e => e.Kind == EdgeKind.Contains)?.Src is not { } owner)
            {
                continue;
            }

            members.Add(new SerializedMember(key, member.Name, member.Kind == NodeKind.Property, owner, Node(owner)?.Name ?? Names.Qualified(owner),
                Names.Qualified(owner), member.LineStart, member.LineEnd));
        }

        return [.. members.OrderBy(m => m.LineStart).ThenBy(m => m.Key, StringComparer.Ordinal)];
    }

    /// <summary>The types declared in a script outside any other type, in line order.</summary>
    public IReadOnlyList<DeclaredType> TypesIn(string file)
    {
        var types = new List<(int Line, DeclaredType Type)>();
        foreach (string key in _store.KeysInFile(file))
        {
            if (Node(key) is not { Kind: NodeKind.Type } type || In(key).Any(e => e.Kind == EdgeKind.Contains))
            {
                continue;
            }

            string qualified = Names.Qualified(key);
            int dot = qualified.LastIndexOf('.');
            bool concrete = Names.Prop(type, "subkind") is "class" or "struct" or "record" or "record_struct"
                && !Names.Flag(type, "abstract") && !Names.Flag(type, "static");
            types.Add((type.LineStart, new DeclaredType(key, type.Name, dot < 0 ? "" : qualified[..dot], qualified, concrete)));
        }

        return [.. types.OrderBy(t => t.Line).Select(t => t.Type)];
    }
}
