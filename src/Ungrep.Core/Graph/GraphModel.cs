namespace Ungrep.Core.Graph;

/// <summary>Node kinds (SPEC-graph-core, Node kinds). The vocabulary is closed.</summary>
public enum NodeKind
{
    Type,
    Method,
    Field,
    Property,
    Event,
    Asset,
    GameObject,
    Component,
    PrefabInstance,
    SubAsset,
}

/// <summary>Edge kinds (SPEC-graph-core, Edge kinds), read `src kind dst`. The vocabulary is closed.</summary>
public enum EdgeKind
{
    Contains,
    ChildOf,
    Inherits,
    Implements,
    Overrides,
    Calls,
    UsesType,
    Dispatches,
    ScriptDefines,
    InstanceOf,
    Places,
    VariantOf,
    OverridesValue,
    References,
    EventCalls,
    AnimEventCalls,
    InAssembly,
    AssemblyRefs,
    Addressable,
}

/// <summary>How sure an edge is: Roslyn or a GUID (exact), or a string or name match (heuristic).</summary>
public enum Confidence
{
    Exact = 1,
    Heuristic = 2,
}

/// <summary>Which indexer owns a file.</summary>
public enum Indexer
{
    Code,
    Asset,
}

/// <summary>What happened when the file was indexed.</summary>
public enum FileStatus
{
    Ok,
    Binary,
    Error,
}

/// <summary>Read-only for queries and hooks that only look; read-write for indexers.</summary>
public enum StoreMode
{
    ReadOnly,
    ReadWrite,
}

/// <summary>
/// One indexed file: project-relative path with forward slashes, the size and mtime it had when
/// indexed, and the SHA-256 of its content as 64 lowercase hex characters.
/// </summary>
public sealed record FileRecord(string Path, long Size, long MtimeNs, string Hash, Indexer Indexer, FileStatus Status = FileStatus.Ok);

/// <summary>
/// A node. <see cref="File"/> is the owning file's path; lines are 1-based, 0 when unknown;
/// <see cref="Props"/> is a small JSON object or null.
/// </summary>
public sealed record Node(string Key, NodeKind Kind, string Name, string File, int LineStart = 0, int LineEnd = 0, string? Props = null);

/// <summary>An edge between two node keys, owned by the file whose parsing produced it.</summary>
public sealed record Edge(string Src, EdgeKind Kind, string Dst, Confidence Confidence, string File, int Line = 0, string? Props = null);

/// <summary>An edge as read back; <see cref="Resolved"/> is false when the far end has no node.</summary>
public sealed record EdgeHit(Edge Edge, bool Resolved);

/// <summary>At most `limit` items, and whether more exist.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, bool More);

/// <summary>Thrown by a read-only open when there is no usable graph: run a full index.</summary>
public sealed class GraphRebuildRequiredException(string message) : Exception("rebuild required: " + message);

/// <summary>Builds node keys (SPEC-graph-core, Node keys). Keys are case-sensitive.</summary>
public static class NodeKeys
{
    /// <summary>A C# symbol from its Roslyn documentation ID, e.g. `T:Purge.Lab.LabStage`.</summary>
    public static string Symbol(string documentationId) => "cs:" + documentationId;

    /// <summary>An asset from the GUID in its `.meta` file.</summary>
    public static string Asset(string guid) => "a:" + guid;

    /// <summary>An object inside an asset: GameObject, component, prefab instance, sub-asset.</summary>
    public static string Object(string guid, long fileId) => $"o:{guid}:{fileId}";

    /// <summary>A file without a GUID, by project-relative path.</summary>
    public static string File(string path) => "p:" + path;

    public static bool IsValid(string? key) =>
        key is not null
        && (key.StartsWith("cs:", StringComparison.Ordinal) && key.Length > 3
            || key.StartsWith("a:", StringComparison.Ordinal) && key.Length > 2
            || key.StartsWith("o:", StringComparison.Ordinal) && key.Length > 2
            || key.StartsWith("p:", StringComparison.Ordinal) && key.Length > 2);
}

/// <summary>The names kinds have in the database (snake_case, as in the spec).</summary>
public static class KindNames
{
    private static readonly string[] _nodes =
    [
        "type", "method", "field", "property", "event", "asset", "game_object", "component", "prefab_instance", "sub_asset",
    ];

    private static readonly string[] _edges =
    [
        "contains", "child_of", "inherits", "implements", "overrides", "calls", "uses_type", "dispatches", "script_defines",
        "instance_of", "places", "variant_of", "overrides_value", "references", "event_calls", "anim_event_calls",
        "in_assembly", "assembly_refs", "addressable",
    ];

    public static string Of(NodeKind kind) =>
        Enum.IsDefined(kind) ? _nodes[(int)kind] : throw new ArgumentException($"unknown node kind {(int)kind}", nameof(kind));

    public static string Of(EdgeKind kind) =>
        Enum.IsDefined(kind) ? _edges[(int)kind] : throw new ArgumentException($"unknown edge kind {(int)kind}", nameof(kind));

    public static NodeKind ParseNode(string name) =>
        Array.IndexOf(_nodes, name) is >= 0 and var i ? (NodeKind)i : throw new FormatException("unknown node kind: " + name);

    public static EdgeKind ParseEdge(string name) =>
        Array.IndexOf(_edges, name) is >= 0 and var i ? (EdgeKind)i : throw new FormatException("unknown edge kind: " + name);
}

/// <summary>A set of node kinds to filter a read by.</summary>
public readonly record struct NodeKindSet(uint Mask)
{
    public static NodeKindSet All { get; } = new(uint.MaxValue);

    public static NodeKindSet Of(params NodeKind[] kinds) => new(kinds.Aggregate(0u, (mask, k) => mask | 1u << (int)k));

    public bool Contains(NodeKind kind) => (Mask & 1u << (int)kind) != 0;

    internal IEnumerable<string> Names => Enum.GetValues<NodeKind>().Where(Contains).Select(KindNames.Of);

    internal bool IsAll => Enum.GetValues<NodeKind>().All(Contains);
}

/// <summary>A set of edge kinds to filter a read by.</summary>
public readonly record struct EdgeKindSet(uint Mask)
{
    public static EdgeKindSet All { get; } = new(uint.MaxValue);

    public static EdgeKindSet Of(params EdgeKind[] kinds) => new(kinds.Aggregate(0u, (mask, k) => mask | 1u << (int)k));

    public bool Contains(EdgeKind kind) => (Mask & 1u << (int)kind) != 0;

    internal IEnumerable<string> Names => Enum.GetValues<EdgeKind>().Where(Contains).Select(KindNames.Of);

    internal bool IsAll => Enum.GetValues<EdgeKind>().All(Contains);
}
