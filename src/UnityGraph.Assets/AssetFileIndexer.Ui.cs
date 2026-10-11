using System.Text.RegularExpressions;
using System.Xml;
using UnityGraph.Core.Graph;

namespace UnityGraph.Assets;

/// <summary>
/// UXML, USS and TSS files (SPEC-asset-index, UI Toolkit files): text, not YAML. Each gives
/// `references` edges from its own asset to the assets it names; no nodes. A path reference reads
/// the target's `.meta` for its GUID, the one read outside the file and its own `.meta`.
/// </summary>
internal static partial class AssetFileIndexer
{
    private const string ProjectDatabase = "project://database/";

    [GeneratedRegex(@"\b(url|resource)\(\s*(?:""([^""]*)""|'([^']*)'|([^)""']*?))\s*\)")]
    private static partial Regex StyleCall();

    [GeneratedRegex(@"@import\s+(?:""([^""]*)""|'([^']*)')")]
    private static partial Regex StringImport();

    [GeneratedRegex(@"@import\s+$")]
    private static partial Regex ImportBefore();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"[?&]guid=([0-9a-fA-F]{32})")]
    private static partial Regex GuidParameter();

    private static FileContribution UiFile(string root, string path, string guid, string text, FileRecord record, List<string> problems, Func<string, IReadOnlyList<string>>? resources)
    {
        var file = new UiReferences(root, path, guid, problems, resources);
        if (path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
        {
            if (!file.Uxml(text))
            {
                return new FileContribution(record with { Status = FileStatus.Error }, [], []);
            }
        }
        else
        {
            file.StyleSheet(text, 1, null);
        }

        return new FileContribution(record, [], file.Edges);
    }

    private sealed class UiReferences(string root, string path, string guid, List<string> problems, Func<string, IReadOnlyList<string>>? resources)
    {
        private readonly string _asset = NodeKeys.Asset(guid);
        private readonly string _folder = path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : "";
        private readonly HashSet<(string Dst, string Field)> _seen = [];

        public List<Edge> Edges { get; } = [];

        // False when the file is not well-formed XML: no rows, a problem line.
        public bool Uxml(string text)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), settings);
                var lines = (IXmlLineInfo)reader;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    string element = reader.LocalName;
                    bool linked = element is "Template" or "Style";
                    for (bool more = reader.MoveToFirstAttribute(); more; more = reader.MoveToNextAttribute())
                    {
                        int line = lines.LineNumber;
                        if (linked && reader.LocalName == "src")
                        {
                            Reference(reader.Value, line, element);
                        }
                        else if (reader.LocalName == "style")
                        {
                            StyleSheet(reader.Value, line, "style");
                        }
                        else if (reader.Value.TrimStart().StartsWith(ProjectDatabase, StringComparison.Ordinal))
                        {
                            Reference(reader.Value, line, reader.LocalName);
                        }
                    }

                    reader.MoveToElement();
                }
            }
            catch (XmlException e)
            {
                problems.Add(path + ":" + e.LineNumber + ": not well-formed XML (" + e.Message + ")");
                return false;
            }

            return true;
        }

        // USS and TSS text, or a UXML `style` attribute (`field` then names every reference `style`).
        public void StyleSheet(string text, int firstLine, string? field)
        {
            // Comments become spaces, newlines kept, so positions and lines still match the file.
            string plain = Comment().Replace(text, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));
            foreach (Match call in StyleCall().Matches(plain))
            {
                string value = call.Groups[2].Success ? call.Groups[2].Value : call.Groups[3].Success ? call.Groups[3].Value : call.Groups[4].Value;
                int line = firstLine + plain.AsSpan(0, call.Index).Count('\n');
                if (call.Groups[1].Value == "resource")
                {
                    Resource(value, line, field ?? "resource");
                }
                else
                {
                    Reference(value, line, field ?? (ImportBefore().IsMatch(plain.AsSpan(0, call.Index).ToString()) ? "@import" : "url"));
                }
            }

            if (field is null)
            {
                foreach (Match import in StringImport().Matches(plain))
                {
                    Reference(import.Groups[1].Success ? import.Groups[1].Value : import.Groups[2].Value, 1 + plain.AsSpan(0, import.Index).Count('\n'), "@import");
                }
            }
        }

        // A URL or path: a project://database URI's GUID, else the file it names; other schemes are built in.
        private void Reference(string reference, int line, string field)
        {
            string value = reference.Trim();
            if (value.Length == 0)
            {
                return;
            }

            if (value.StartsWith(ProjectDatabase, StringComparison.Ordinal))
            {
                if (GuidParameter().Match(value) is { Success: true } g)
                {
                    Edge(g.Groups[1].Value.ToLowerInvariant(), line, field, Confidence.Exact);
                    return;
                }

                value = "/" + Uri.UnescapeDataString(value[ProjectDatabase.Length..].Split('?', '#')[0]);
            }
            else if (value.Contains("://", StringComparison.Ordinal) || value.StartsWith("data:", StringComparison.Ordinal))
            {
                return;
            }

            value = value.Split('?', '#')[0];
            string? target = FileOf(value);
            if (target is null)
            {
                if (!InPackageCache(value))
                {
                    problems.Add(path + ":" + line + ": " + value + " not found");
                }
            }
            else if (GuidOf(target) is { } targetGuid)
            {
                Edge(targetGuid, line, field, Confidence.Exact);
            }
            else
            {
                problems.Add(path + ":" + line + ": " + target + " has no .meta with a guid");
            }
        }

        // `resource("Images/bg")`: every asset under a Resources folder at that path, without extension.
        private void Resource(string name, int line, string field)
        {
            string key = name.Trim().Trim('/');
            IReadOnlyList<string> found = key.Length == 0 || resources is null ? [] : resources(key);
            if (found.Count == 0)
            {
                problems.Add(path + ":" + line + ": resource " + key + " is in no Resources folder");
                return;
            }

            foreach (string target in found)
            {
                if (GuidOf(target) is { } targetGuid)
                {
                    Edge(targetGuid, line, field, Confidence.Heuristic);
                }
            }
        }

        // The project path a path reference names: relative to the file's folder, else from the project root.
        private string? FileOf(string reference)
        {
            string trimmed = reference.TrimStart('/');
            bool rooted = trimmed.StartsWith("Assets/", StringComparison.Ordinal) || trimmed.StartsWith("Packages/", StringComparison.Ordinal);
            if (!reference.StartsWith('/') && Normal(_folder + "/" + reference) is { } relative && Exists(relative))
            {
                return relative;
            }

            return rooted && Normal(trimmed) is { } fromRoot && Exists(fromRoot) ? fromRoot : null;
        }

        // `Assets/UI/../Art/bg.png` → `Assets/Art/bg.png`; null when `..` leaves the project.
        private static string? Normal(string projectPath)
        {
            var parts = new List<string>();
            foreach (string part in projectPath.Split('/'))
            {
                if (part is "" or ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    continue;
                }

                parts.Add(part);
            }

            return string.Join('/', parts);
        }

        private bool Exists(string projectPath) => File.Exists(Path.Combine(root, projectPath));

        // A package that is not embedded lives in `Library/PackageCache`, which the asset index does not read.
        private bool InPackageCache(string reference)
        {
            string[] parts = reference.TrimStart('/').Split('/', 3);
            return parts.Length == 3 && parts[0] == "Packages" && !Directory.Exists(Path.Combine(root, "Packages", parts[1]));
        }

        private string? GuidOf(string projectPath)
        {
            try
            {
                string meta = Path.Combine(root, projectPath) + ".meta";
                return File.Exists(meta) ? MetaFile.Read(meta).Guid : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private void Edge(string targetGuid, int line, string field, Confidence confidence)
        {
            if (targetGuid == guid || IsBuiltin(targetGuid) || !_seen.Add((targetGuid, field)))
            {
                return;
            }

            var props = new Props();
            props.Text("field", field);
            Edges.Add(new Edge(_asset, EdgeKind.References, NodeKeys.Asset(targetGuid), confidence, path, line, props.ToJson()));
        }
    }
}
