using System.Globalization;
using System.Text.RegularExpressions;

namespace Ungrep.Core;

/// <summary>
/// How Unity writes assets (EditorSettings, m_SerializationMode). Only text assets can be indexed
/// (SPEC Decisions #1).
/// </summary>
public enum AssetSerialization
{
    Mixed = 0,
    ForceBinary = 1,
    ForceText = 2,
}

/// <summary>
/// A Unity project on disk: the folder holding Assets/ and ProjectSettings/ProjectVersion.txt.
/// </summary>
public sealed partial record UnityProject(string Root, string? EditorVersion, AssetSerialization? Serialization)
{
    /// <summary>Oldest supported editor (SPEC Decisions #1).</summary>
    public static readonly Version MinimumVersion = new(2022, 3);

    public bool IsSupportedVersion =>
        ParseVersion(EditorVersion) is { } version && version >= MinimumVersion;

    public string GraphPath => Path.Combine(Root, "Library", "ungrep", "graph.db");

    /// <summary>The nearest Unity project at or above <paramref name="start"/>, or null.</summary>
    public static UnityProject? Find(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            string versionFile = Path.Combine(dir.FullName, "ProjectSettings", "ProjectVersion.txt");
            if (Directory.Exists(Path.Combine(dir.FullName, "Assets")) && File.Exists(versionFile))
            {
                string editorSettings = Path.Combine(dir.FullName, "ProjectSettings", "EditorSettings.asset");
                return new UnityProject(
                    dir.FullName,
                    ParseEditorVersion(File.ReadAllText(versionFile)),
                    File.Exists(editorSettings) ? ParseSerialization(File.ReadAllText(editorSettings)) : null);
            }
        }

        return null;
    }

    /// <summary>`m_EditorVersion: 2022.3.62f1` → "2022.3.62f1".</summary>
    public static string? ParseEditorVersion(string projectVersionTxt) =>
        EditorVersionRegex().Match(projectVersionTxt) is { Success: true } m ? m.Groups[1].Value : null;

    public static AssetSerialization? ParseSerialization(string editorSettingsAsset) =>
        SerializationRegex().Match(editorSettingsAsset) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int mode)
        && Enum.IsDefined(typeof(AssetSerialization), mode)
            ? (AssetSerialization)mode
            : null;

    /// <summary>"6000.3.9f1" → 6000.3; null when it doesn't start with major.minor.</summary>
    public static Version? ParseVersion(string? editorVersion) =>
        editorVersion is not null && MajorMinorRegex().Match(editorVersion) is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;

    [GeneratedRegex(@"^m_EditorVersion:\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex EditorVersionRegex();

    [GeneratedRegex(@"^\s*m_SerializationMode:\s*(\d+)", RegexOptions.Multiline)]
    private static partial Regex SerializationRegex();

    [GeneratedRegex(@"^(\d+)\.(\d+)")]
    private static partial Regex MajorMinorRegex();
}
