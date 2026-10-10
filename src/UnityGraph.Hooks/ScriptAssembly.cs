using System.Text.Json;

namespace UnityGraph.Hooks;

/// <summary>
/// Which assembly Unity compiles a script folder into: the nearest `.asmdef` (or the one an
/// `.asmref` points at) at or above it, else Unity's predefined assemblies by folder name.
/// </summary>
internal static class ScriptAssembly
{
    private static readonly string[] _firstPass = ["Assets/Plugins", "Assets/Standard Assets", "Assets/Pro Standard Assets"];

    /// <summary>
    /// The assembly of a project folder, which may not exist yet (a move's destination); null when
    /// it can't be read (a broken `.asmdef`) or a package folder has none (not compiled).
    /// </summary>
    public static string? Of(GuardContext context, string folder)
    {
        for (string dir = folder; dir.Length > 0; dir = Parent(dir))
        {
            string full = context.FullPath(dir);
            if (Directory.Exists(full))
            {
                if (Directory.EnumerateFiles(full, "*.asmdef").Order(StringComparer.Ordinal).FirstOrDefault() is { } asmdef)
                {
                    return Read(asmdef, "name");
                }

                if (Directory.EnumerateFiles(full, "*.asmref").Order(StringComparer.Ordinal).FirstOrDefault() is { } asmref)
                {
                    return Reference(context, Read(asmref, "reference"));
                }
            }

            if (dir == "Assets" || dir.StartsWith("Packages/", StringComparison.Ordinal) && dir.Count(c => c == '/') == 1)
            {
                break;
            }
        }

        if (!folder.StartsWith("Assets", StringComparison.Ordinal))
        {
            return null;
        }

        bool editor = ("/" + folder + "/").Contains("/Editor/", StringComparison.Ordinal);
        bool firstPass = _firstPass.Any(p => folder == p || folder.StartsWith(p + "/", StringComparison.Ordinal));
        return "Assembly-CSharp" + (firstPass ? editor ? "-Editor-firstpass" : "-firstpass" : editor ? "-Editor" : "");
    }

    public static string Parent(string path) => path.LastIndexOf('/') is > 0 and int slash ? path[..slash] : "";

    // `"reference": "GUID:..."` names the asmdef by GUID; else by assembly name.
    private static string? Reference(GuardContext context, string? reference) =>
        reference is null ? null
        : !reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase) ? reference
        : context.AssetPathOf(reference[5..]) is { } asmdef && File.Exists(context.FullPath(asmdef)) ? Read(context.FullPath(asmdef), "name")
        : null;

    private static string? Read(string path, string property)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
