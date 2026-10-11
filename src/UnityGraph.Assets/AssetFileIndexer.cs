using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityGraph.Core.Graph;

namespace UnityGraph.Assets;

/// <summary>
/// Turns one `.meta` file or one Unity YAML content file into graph rows (SPEC-asset-index, Node
/// rules and Edge rules). Reads nothing but that file and, for a content file, its own `.meta`;
/// a UXML, USS or TSS file also reads the `.meta` of each file it names (AssetFileIndexer.Ui.cs).
/// </summary>
internal static partial class AssetFileIndexer
{
    private const int ClassGameObject = 1;
    private const int ClassTransform = 4;
    private const int ClassMonoBehaviour = 114;
    private const int ClassRectTransform = 224;
    private const int ClassPrefabInstance = 1001;


    private static readonly HashSet<string> _structural = new(StringComparer.Ordinal)
    {
        "m_GameObject", "m_Father", "m_Children", "m_Component", "m_PrefabInstance", "m_PrefabAsset",
        "m_CorrespondingSourceObject", "m_Script", "m_SourcePrefab", "m_Modification",
    };

    private static readonly string[] _layout =
    [
        "m_LocalPosition", "m_LocalRotation", "m_LocalScale", "m_LocalEulerAnglesHint", "m_RootOrder",
        "m_AnchorMin", "m_AnchorMax", "m_AnchoredPosition", "m_SizeDelta", "m_Pivot",
    ];

    /// <summary>The asset node a `.meta` file gives.</summary>
    public static FileContribution Meta(string root, string metaPath, List<string> problems)
    {
        (FileRecord record, byte[]? bytes) = Load(root, metaPath, problems);
        if (bytes is null)
        {
            return new FileContribution(record, [], []);
        }

        MetaFile meta = MetaFile.Parse(new StringReader(Encoding.UTF8.GetString(bytes)));
        if (meta.Folder)
        {
            return new FileContribution(record, [], []);
        }

        if (meta.Guid is null)
        {
            problems.Add(metaPath + ": no guid");
            return new FileContribution(record with { Status = FileStatus.Error }, [], []);
        }

        string asset = metaPath[..^".meta".Length];
        var props = new Props();
        props.Text("asset_type", AssetTypes.Of(asset, meta.MainObjectFileId));
        var node = new Node(NodeKeys.Asset(meta.Guid), NodeKind.Asset, Path.GetFileName(asset), metaPath, 0, 0, props.ToJson());
        return new FileContribution(record, [node], []);
    }

    /// <summary>
    /// The objects and edges inside a scene, prefab or other Unity YAML asset, or the references of a
    /// UXML, USS or TSS file; <paramref name="resources"/> finds `resource(...)` targets for those.
    /// </summary>
    public static FileContribution Content(string root, string path, List<string> problems, Func<string, IReadOnlyList<string>>? resources = null)
    {
        (FileRecord record, byte[]? bytes) = Load(root, path, problems);
        if (bytes is null)
        {
            return new FileContribution(record, [], []);
        }

        MetaFile? meta = null;
        try
        {
            string metaPath = Path.Combine(root, path + ".meta");
            meta = File.Exists(metaPath) ? MetaFile.Read(metaPath) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        if (meta?.Guid is not { } guid)
        {
            problems.Add(path + ": no .meta with a guid (not imported by Unity yet?)");
            return new FileContribution(record with { Status = FileStatus.Error }, [], []);
        }

        string text = Encoding.UTF8.GetString(bytes);
        if (AssetTypes.IsUiFile(path))
        {
            return UiFile(root, path, guid, text.TrimStart('﻿'), record, problems, resources);
        }

        if (!text.StartsWith("%YAML", StringComparison.Ordinal))
        {
            return new FileContribution(record with { Status = FileStatus.Binary }, [], []);
        }

        var readProblems = new List<string>();
        IReadOnlyList<YamlDocument> documents = UnityYaml.Read(new StringReader(text), readProblems);
        problems.AddRange(readProblems.Select(p => path + ": " + p));
        var content = new ContentIndexer(path, guid, meta.MainObjectFileId, documents, problems);
        content.Run();
        return new FileContribution(record, content.Nodes, content.Edges);
    }

    private static (FileRecord Record, byte[]? Bytes) Load(string root, string path, List<string> problems)
    {
        FileStat? stat = FileStat.Of(root, path);
        try
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, path));
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return (new FileRecord(path, stat?.Size ?? bytes.Length, stat?.MtimeNs ?? 0, hash, Indexer.Asset), bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{path}: {e.Message}");
            string empty = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
            return (new FileRecord(path, stat?.Size ?? 0, stat?.MtimeNs ?? 0, empty, Indexer.Asset, FileStatus.Error), null);
        }
    }

    [GeneratedRegex(@"^(.+)\.m_PersistentCalls\.m_Calls\[(\d+)\]\.(m_Target|m_TargetAssemblyTypeName|m_MethodName|m_Mode|m_Arguments\.m_ObjectArgumentAssemblyTypeName)$")]
    private static partial Regex PersistentCall();

    [GeneratedRegex(@"^m_SerializeEntries\[(\d+)\]\.(m_GUID|m_Address|m_SerializedLabels\[\d+\])$")]
    private static partial Regex AddressableEntry();

    [GeneratedRegex(@"^m_Modification\.m_Modifications\[(\d+)\]\.(target|propertyPath|value|objectReference)$")]
    private static partial Regex Modification();

    private static string Head(string path)
    {
        int end = path.IndexOfAny(['.', '[']);
        return end < 0 ? path : path[..end];
    }


    private static bool IsBuiltin(string guid) => guid.StartsWith("0000000000000000", StringComparison.Ordinal);

    private sealed class ContentIndexer(string path, string guid, long? mainObject, IReadOnlyList<YamlDocument> documents, List<string> problems)
    {
        private readonly string _asset = NodeKeys.Asset(guid);
        private readonly bool _hierarchy = AssetTypes.IsHierarchy(path);
        private readonly bool _prefab = path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        private readonly Dictionary<long, YamlDocument> _byId = [];
        private readonly Dictionary<YamlDocument, Dictionary<string, YamlLeaf>> _leaves = [];
        private readonly Dictionary<long, long> _transformObject = [];  // transform fileID → GameObject fileID
        private readonly Dictionary<long, long> _objectTransform = [];  // GameObject fileID → transform fileID
        private readonly Dictionary<long, string> _instanceNames = [];
        private readonly Dictionary<long, string?> _paths = [];
        private readonly HashSet<(string, EdgeKind, string, string?)> _seen = [];
        private int _untypedCalls;

        public List<Node> Nodes { get; } = [];

        public List<Edge> Edges { get; } = [];

        public void Run()
        {
            foreach (YamlDocument document in documents)
            {
                if (!_byId.TryAdd(document.FileId, document))
                {
                    problems.Add($"{path}: fileID {document.FileId} appears twice (line {document.LineStart}); first kept");
                    continue;
                }

                var leaves = new Dictionary<string, YamlLeaf>(StringComparer.Ordinal);
                foreach (YamlLeaf leaf in document.Leaves)
                {
                    leaves.TryAdd(leaf.Path, leaf);
                }

                _leaves[document] = leaves;
                if (document.ClassId is ClassTransform or ClassRectTransform && !document.Stripped
                    && RefOf(document, "m_GameObject") is { FileId: not 0 } owner)
                {
                    _transformObject[document.FileId] = owner.FileId;
                    _objectTransform.TryAdd(owner.FileId, document.FileId);
                }
                else if (document.ClassId == ClassPrefabInstance)
                {
                    _instanceNames[document.FileId] = InstanceName(document);
                }
            }

            YamlDocument? main = !_hierarchy ? MainDocument() : null;
            foreach (YamlDocument document in documents)
            {
                if (_byId[document.FileId] != document || document.Stripped)
                {
                    continue;
                }

                if (document.ClassId == ClassGameObject)
                {
                    GameObject(document);
                }
                else if (document.ClassId == ClassPrefabInstance)
                {
                    PrefabInstance(document);
                }
                else if (document.ClassId is ClassTransform or ClassRectTransform)
                {
                    References(document, Local(document.FileId), prefix: "");
                }
                else if (RefOf(document, "m_GameObject") is { FileId: not 0 } owner)
                {
                    Component(document, owner.FileId);
                }
                else if (document == main)
                {
                    if (ScriptOf(document) is { } script)
                    {
                        Edge(_asset, EdgeKind.InstanceOf, NodeKeys.Asset(script), document.LineStart);
                    }

                    References(document, _asset, prefix: "");
                    Events(document, _asset);
                    Addressables(document);
                }
                else if (document.ClassId == ClassMonoBehaviour)
                {
                    SubAsset(document);
                }
                else
                {
                    References(document, _asset, prefix: document.ClassName + ".");
                }
            }

            if (_untypedCalls > 0)
            {
                problems.Add($"{path}: {_untypedCalls} UnityEvent call(s) name no type; skipped");
            }
        }

        private YamlDocument? MainDocument() =>
            documents.FirstOrDefault(d => d.FileId == mainObject && !d.Stripped) ?? documents.FirstOrDefault(d => !d.Stripped);

        private void GameObject(YamlDocument document)
        {
            string key = ObjectKey(document.FileId);
            var props = new Props();
            props.Text("path", PathOf(document.FileId) ?? "");
            props.Flag("active", false, Text(document, "m_IsActive") == "0");
            Nodes.Add(new Node(key, NodeKind.GameObject, NameOf(document), path, document.LineStart, document.LineEnd, props.ToJson()));
            Edge(_asset, EdgeKind.Contains, key, document.LineStart);
            if (_objectTransform.TryGetValue(document.FileId, out long transform)
                && RefOf(_byId[transform], "m_Father") is { FileId: not 0 } father
                && Parent(father.FileId) is { } parent)
            {
                Edge(key, EdgeKind.ChildOf, parent, _byId[transform].LineStart);
            }

            References(document, key, prefix: "");
        }

        private void Component(YamlDocument document, long owner)
        {
            string key = ObjectKey(document.FileId);
            var props = new Props();
            props.Number("class_id", document.ClassId);
            string? fullName = ClassOf(document);
            props.Text("class", fullName);
            string? script = ScriptOf(document);
            props.Text("script", script);
            props.Flag("enabled", false, Text(document, "m_Enabled") == "0");
            Nodes.Add(new Node(key, NodeKind.Component, TypeLabel(document), path, document.LineStart, document.LineEnd, props.ToJson()));
            Edge(Local(owner), EdgeKind.Contains, key, document.LineStart);
            if (script is not null)
            {
                Edge(key, EdgeKind.InstanceOf, NodeKeys.Asset(script), LineOf(document, "m_Script"));
            }

            References(document, key, prefix: "");
            Events(document, key);
        }

        private void SubAsset(YamlDocument document)
        {
            string key = ObjectKey(document.FileId);
            var props = new Props();
            props.Text("class", ClassOf(document));
            string? script = ScriptOf(document);
            props.Text("script", script);
            Nodes.Add(new Node(key, NodeKind.SubAsset, NameOf(document), path, document.LineStart, document.LineEnd, props.ToJson()));
            Edge(_asset, EdgeKind.Contains, key, document.LineStart);
            if (script is not null)
            {
                Edge(key, EdgeKind.InstanceOf, NodeKeys.Asset(script), LineOf(document, "m_Script"));
            }

            References(document, key, prefix: "");
            Events(document, key);
        }

        private void PrefabInstance(YamlDocument document)
        {
            string key = ObjectKey(document.FileId);
            ObjectRef? source = RefOf(document, "m_SourcePrefab");
            string? sourceGuid = source?.Guid is { } g && MetaFile.IsGuid(g) ? g : null;
            var props = new Props();
            props.Text("source", sourceGuid);
            Nodes.Add(new Node(key, NodeKind.PrefabInstance, _instanceNames[document.FileId], path, document.LineStart, document.LineEnd, props.ToJson()));
            Edge(_asset, EdgeKind.Places, key, document.LineStart);
            if (sourceGuid is not null)
            {
                Edge(key, EdgeKind.InstanceOf, NodeKeys.Asset(sourceGuid), LineOf(document, "m_SourcePrefab"));
            }

            ObjectRef? parent = RefOf(document, "m_Modification.m_TransformParent");
            if (parent is { FileId: not 0 } && Parent(parent.Value.FileId) is { } parentKey)
            {
                Edge(key, EdgeKind.ChildOf, parentKey, LineOf(document, "m_Modification.m_TransformParent"));
            }
            else if (_prefab && sourceGuid is not null)
            {
                Edge(_asset, EdgeKind.VariantOf, NodeKeys.Asset(sourceGuid), LineOf(document, "m_SourcePrefab"));
            }

            Modifications(document, key);
        }

        private void Modifications(YamlDocument document, string instance)
        {
            var rows = new SortedDictionary<int, Dictionary<string, YamlLeaf>>();
            foreach (YamlLeaf leaf in document.Leaves)
            {
                if (Modification().Match(leaf.Path) is { Success: true } m)
                {
                    int index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (!rows.TryGetValue(index, out var row))
                    {
                        rows[index] = row = new Dictionary<string, YamlLeaf>(StringComparer.Ordinal);
                    }

                    row.TryAdd(m.Groups[2].Value, leaf);
                }
            }

            foreach (Dictionary<string, YamlLeaf> row in rows.Values)
            {
                if (row.GetValueOrDefault("target")?.Ref is not { FileId: not 0, Guid: { } targetGuid } target
                    || row.GetValueOrDefault("propertyPath")?.Value is not { Length: > 0 } property
                    || _layout.Any(l => property.StartsWith(l, StringComparison.Ordinal)))
                {
                    continue;
                }

                int line = row["target"].Line;
                var props = new Props();
                props.Text("path", property);
                string? referenced = row.GetValueOrDefault("objectReference")?.Ref is { FileId: not 0 } r && Target(r) is { } dst ? dst : null;
                if (referenced is not null)
                {
                    props.Text("ref", referenced);
                }
                else
                {
                    props.Text("value", row.GetValueOrDefault("value")?.Value ?? "");
                }

                Edge(instance, EdgeKind.OverridesValue, NodeKeys.Object(targetGuid, target.FileId), line, props.ToJson());
                if (referenced is not null)
                {
                    ReferenceEdge(instance, row["objectReference"].Ref!.Value, property, row["objectReference"].Line);
                }
            }
        }

        private void References(YamlDocument document, string src, string prefix)
        {
            foreach (YamlLeaf leaf in document.Leaves)
            {
                if (leaf.Ref is not { FileId: not 0 } reference
                    || _structural.Contains(Head(leaf.Path))
                    || (leaf.Path.EndsWith(".m_Target", StringComparison.Ordinal) && leaf.Path.Contains(".m_PersistentCalls.m_Calls[", StringComparison.Ordinal)))
                {
                    continue;
                }

                ReferenceEdge(src, reference, prefix + leaf.Path, leaf.Line);
            }
        }

        private void ReferenceEdge(string src, ObjectRef reference, string field, int line)
        {
            if (Target(reference) is not { } dst)
            {
                return;
            }

            var props = new Props();
            props.Text("field", field);
            if (dst.StartsWith("a:", StringComparison.Ordinal))
            {
                props.Long("object", reference.FileId);
            }

            Edge(src, EdgeKind.References, dst, line, props.ToJson());
        }

        // The key a reference points at: the other asset, or the local object; null for built-ins.
        private string? Target(ObjectRef reference)
        {
            if (reference.Guid is null || reference.Guid == guid)
            {
                return Local(reference.FileId);
            }

            return MetaFile.IsGuid(reference.Guid) && !IsBuiltin(reference.Guid) ? NodeKeys.Asset(reference.Guid) : null;
        }

        private void Events(YamlDocument document, string src)
        {
            var calls = new SortedDictionary<(string Event, int Index), Dictionary<string, YamlLeaf>>();
            foreach (YamlLeaf leaf in document.Leaves)
            {
                if (PersistentCall().Match(leaf.Path) is { Success: true } m)
                {
                    var id = (m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
                    if (!calls.TryGetValue(id, out var call))
                    {
                        calls[id] = call = new Dictionary<string, YamlLeaf>(StringComparer.Ordinal);
                    }

                    call.TryAdd(m.Groups[3].Value, leaf);
                }
            }

            foreach (var (id, call) in calls.OrderBy(c => c.Value.Values.Min(l => l.Line)))
            {
                string eventPath = id.Event;
                if (call.GetValueOrDefault("m_MethodName")?.Value is not { Length: > 0 } method)
                {
                    continue;
                }

                ObjectRef? target = call.GetValueOrDefault("m_Target")?.Ref;
                string? type = TypeName(call.GetValueOrDefault("m_TargetAssemblyTypeName")?.Value)
                    ?? (target is { FileId: not 0, Guid: null } t && _byId.TryGetValue(t.FileId, out YamlDocument? targetDocument) ? ClassOf(targetDocument) : null);
                if (type is null)
                {
                    _untypedCalls++;
                    continue;
                }

                string parameters = call.GetValueOrDefault("m_Mode")?.Value switch
                {
                    "2" => "(" + (TypeName(call.GetValueOrDefault("m_Arguments.m_ObjectArgumentAssemblyTypeName")?.Value) ?? "UnityEngine.Object") + ")",
                    "3" => "(System.Int32)",
                    "4" => "(System.Single)",
                    "5" => "(System.String)",
                    "6" => "(System.Boolean)",
                    _ => "",
                };
                bool dynamic = call.GetValueOrDefault("m_Mode")?.Value is not ("1" or "2" or "3" or "4" or "5" or "6");
                var props = new Props();
                props.Text("event", eventPath);
                if (target is { FileId: not 0 } r && Target(r) is { } targetKey)
                {
                    props.Text("target", targetKey);
                }

                Edge(src, EdgeKind.EventCalls, NodeKeys.Symbol("M:" + type + "." + method + parameters), call["m_MethodName"].Line, props.ToJson(),
                    dynamic ? Confidence.Heuristic : Confidence.Exact);
            }
        }

        private void Addressables(YamlDocument document)
        {
            var entries = new SortedDictionary<int, (string? Guid, string? Address, List<string> Labels, int Line)>();
            foreach (YamlLeaf leaf in document.Leaves)
            {
                if (AddressableEntry().Match(leaf.Path) is not { Success: true } m)
                {
                    continue;
                }

                int index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var entry = entries.GetValueOrDefault(index, (null, null, [], leaf.Line));
                switch (m.Groups[2].Value)
                {
                    case "m_GUID":
                        entry.Guid = leaf.Value;
                        break;
                    case "m_Address":
                        entry.Address = leaf.Value;
                        break;
                    default:
                        entry.Labels.Add(leaf.Value);
                        break;
                }

                entries[index] = entry;
            }

            foreach (var entry in entries.Values)
            {
                if (entry.Guid is { } entryGuid && MetaFile.IsGuid(entryGuid))
                {
                    var props = new Props();
                    props.Text("address", entry.Address ?? "");
                    props.List("labels", [.. entry.Labels]);
                    Edge(NodeKeys.Asset(entryGuid), EdgeKind.Addressable, _asset, entry.Line, props.ToJson());
                }
            }
        }

        // The key an object in this file is known by: a Transform means its GameObject, and an
        // object of a nested prefab means that object in the source prefab.
        private string Local(long fileId)
        {
            if (_transformObject.TryGetValue(fileId, out long owner))
            {
                fileId = owner;
            }

            if (_byId.TryGetValue(fileId, out YamlDocument? document) && document.Stripped
                && RefOf(document, "m_CorrespondingSourceObject") is { FileId: not 0, Guid: { } source } && MetaFile.IsGuid(source))
            {
                return NodeKeys.Object(source, RefOf(document, "m_CorrespondingSourceObject")!.Value.FileId);
            }

            return ObjectKey(fileId);
        }

        // A parent transform: a local one means its GameObject; a stripped one, its prefab instance.
        private string? Parent(long transform)
        {
            if (_byId.TryGetValue(transform, out YamlDocument? document) && document.Stripped
                && RefOf(document, "m_PrefabInstance") is { FileId: not 0 } instance)
            {
                return ObjectKey(instance.FileId);
            }

            return _transformObject.TryGetValue(transform, out long owner) ? ObjectKey(owner) : null;
        }

        private string? PathOf(long gameObject) => PathOf(gameObject, 0);

        private string? PathOf(long gameObject, int depth)
        {
            if (_paths.TryGetValue(gameObject, out string? known))
            {
                return known;
            }

            if (depth > 100 || !_byId.TryGetValue(gameObject, out YamlDocument? document))
            {
                return null;
            }

            string name = NameOf(document);
            string? parent = null;
            if (_objectTransform.TryGetValue(gameObject, out long transform) && RefOf(_byId[transform], "m_Father") is { FileId: not 0 } father)
            {
                parent = ParentPath(father.FileId, depth + 1);
            }

            string result = parent is null ? name : parent + "/" + name;
            _paths[gameObject] = result;
            return result;
        }

        private string? ParentPath(long transform, int depth)
        {
            if (_byId.TryGetValue(transform, out YamlDocument? document) && document.Stripped
                && RefOf(document, "m_PrefabInstance") is { FileId: not 0 } instance && _byId.TryGetValue(instance.FileId, out YamlDocument? instanceDocument))
            {
                string name = _instanceNames.GetValueOrDefault(instance.FileId, "");
                return RefOf(instanceDocument, "m_Modification.m_TransformParent") is { FileId: not 0 } up && depth < 100
                    && ParentPath(up.FileId, depth + 1) is { } above
                    ? above + "/" + name
                    : name;
            }

            return _transformObject.TryGetValue(transform, out long owner) ? PathOf(owner, depth) : null;
        }

        private string InstanceName(YamlDocument document)
        {
            var names = new List<string>();
            var rows = new Dictionary<string, (string? Property, string? Value)>(StringComparer.Ordinal);
            foreach (YamlLeaf leaf in document.Leaves)
            {
                if (Modification().Match(leaf.Path) is { Success: true } m && m.Groups[2].Value is "propertyPath" or "value")
                {
                    var row = rows.GetValueOrDefault(m.Groups[1].Value);
                    rows[m.Groups[1].Value] = m.Groups[2].Value == "propertyPath" ? (leaf.Value, row.Value) : (row.Property, leaf.Value);
                }
            }

            names.AddRange(rows.Values.Where(r => r.Property == "m_Name" && r.Value is not null).Select(r => r.Value!));
            if (names.Count == 1 && names[0].Length > 0)
            {
                return names[0];
            }

            string? source = RefOf(document, "m_SourcePrefab")?.Guid;
            return source is { Length: >= 8 } ? "prefab " + source[..8] : "prefab";
        }

        private string ObjectKey(long fileId) => NodeKeys.Object(guid, fileId);

        private void Edge(string src, EdgeKind kind, string dst, int line, string? props = null, Confidence confidence = Confidence.Exact)
        {
            if (_seen.Add((src, kind, dst, props)))
            {
                Edges.Add(new Edge(src, kind, dst, confidence, path, line, props));
            }
        }

        private string? Text(YamlDocument document, string leafPath) => _leaves[document].GetValueOrDefault(leafPath)?.Value;

        private ObjectRef? RefOf(YamlDocument document, string leafPath) => _leaves[document].GetValueOrDefault(leafPath)?.Ref;

        private int LineOf(YamlDocument document, string leafPath) => _leaves[document].GetValueOrDefault(leafPath)?.Line ?? document.LineStart;

        private string? ScriptOf(YamlDocument document) =>
            RefOf(document, "m_Script") is { Guid: { } script } && MetaFile.IsGuid(script) && !IsBuiltin(script) ? script : null;

        // The full type name Unity recorded for a MonoBehaviour: `Assembly::Namespace.Type`.
        // The short script class for a MonoBehaviour, else the YAML class (`MeshRenderer`).
        private string TypeLabel(YamlDocument document) => document.ClassId == ClassMonoBehaviour
            ? ClassOf(document) is { } full ? full[(full.LastIndexOf('.') + 1)..] : "MonoBehaviour"
            : document.ClassName;

        // Unity allows a blank `m_Name` (hidden sub-assets, unnamed objects); a node needs a name.
        private string NameOf(YamlDocument document) => Text(document, "m_Name") is { Length: > 0 } name ? name : TypeLabel(document);

        private string? ClassOf(YamlDocument document)
        {
            string? identifier = Text(document, "m_EditorClassIdentifier");
            if (string.IsNullOrEmpty(identifier))
            {
                return null;
            }

            int split = identifier.IndexOf("::", StringComparison.Ordinal);
            string name = split < 0 ? identifier : identifier[(split + 2)..];
            return name.Length == 0 ? null : name;
        }

        // `Namespace.Outer+Inner, Assembly` → `Namespace.Outer.Inner`.
        private static string? TypeName(string? assemblyQualified)
        {
            if (string.IsNullOrWhiteSpace(assemblyQualified))
            {
                return null;
            }

            int comma = assemblyQualified.IndexOf(',', StringComparison.Ordinal);
            string name = (comma < 0 ? assemblyQualified : assemblyQualified[..comma]).Trim().Replace('+', '.');
            return name.Length == 0 ? null : name;
        }
    }

    /// <summary>A node's or edge's props, written in the order added; null when empty.</summary>
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

        // Written only when the value differs from the default, e.g. `active: false`.
        public void Flag(string key, bool value, bool when)
        {
            if (when)
            {
                _items.Add((key, value));
            }
        }

        public void Number(string key, int value) => _items.Add((key, value));

        public void Long(string key, long value) => _items.Add((key, value));

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
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
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
                        case long n:
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
