namespace Ungrep.Assets;

/// <summary>Which files asset-index reads, and the `asset_type` of an asset (SPEC-asset-index).</summary>
public static class AssetTypes
{
    /// <summary>ScriptableObject main object fileID in a `.asset`'s NativeFormatImporter.</summary>
    public const long ScriptableObjectMain = 11400000;

    private static readonly HashSet<string> _content = new(StringComparer.OrdinalIgnoreCase)
    {
        ".unity", ".prefab", ".asset", ".mat", ".controller", ".overrideController", ".anim", ".mask",
        ".playable", ".mixer", ".spriteatlas", ".physicMaterial", ".physicsMaterial2D", ".lighting",
        ".preset", ".signal", ".renderTexture", ".terrainlayer", ".flare", ".guiskin", ".brush",
        ".uxml", ".uss", ".tss",
    };

    private static readonly Dictionary<string, string> _types = Build(
        ("script", [".cs"]),
        ("scene", [".unity"]),
        ("prefab", [".prefab"]),
        ("material", [".mat"]),
        ("shader", [".shader", ".shadergraph", ".shadersubgraph", ".compute", ".hlsl", ".cginc"]),
        ("animator_controller", [".controller", ".overrideController"]),
        ("animation_clip", [".anim"]),
        ("input_actions", [".inputactions"]),
        ("asmdef", [".asmdef", ".asmref"]),
        ("texture", [".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".hdr", ".bmp", ".gif"]),
        ("audio", [".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac"]),
        ("model", [".fbx", ".obj", ".blend", ".dae", ".3ds", ".max", ".ma", ".mb"]));

    /// <summary>A Unity YAML asset, or a UI Toolkit text file, whose content is indexed when it isn't vendored.</summary>
    public static bool IsContent(string path) => _content.Contains(Path.GetExtension(path));

    /// <summary>UXML, USS and TSS: read as text, not YAML (SPEC-asset-index, UI Toolkit files).</summary>
    public static bool IsUiFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".uxml" or ".uss" or ".tss";

    /// <summary>Scenes and prefabs: GameObject hierarchies rather than one main object.</summary>
    public static bool IsHierarchy(string path) =>
        path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

    /// <summary>The `asset_type` of the asset at <paramref name="path"/> (not its `.meta`).</summary>
    public static string Of(string path, long? mainObjectFileId)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".asset", StringComparison.OrdinalIgnoreCase))
        {
            return mainObjectFileId == ScriptableObjectMain ? "scriptable_object" : "other";
        }

        return _types.GetValueOrDefault(extension, "other");
    }

    private static Dictionary<string, string> Build(params (string Type, string[] Extensions)[] rows)
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string type, string[] extensions) in rows)
        {
            foreach (string extension in extensions)
            {
                types.Add(extension, type);
            }
        }

        return types;
    }
}
