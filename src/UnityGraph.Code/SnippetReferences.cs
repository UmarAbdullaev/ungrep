using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnityGraph.Code;

/// <summary>
/// What `ug eval` compiles against when the bridge cannot list the editor's assemblies
/// (SPEC-editor-link, Running code): the project's script assemblies and the editor's own, found
/// from the running editor or Unity Hub's install folder.
/// </summary>
public static partial class SnippetReferences
{
    /// <summary>
    /// Every managed DLL in `Library/ScriptAssemblies`, the editor's `Managed/UnityEngine` and
    /// `Managed`, and its `MonoBleedingEdge/lib/mono/unityjit-*` runtime with `Facades`; one per
    /// file name, the first found.
    /// </summary>
    public static IReadOnlyList<string> Fallback(string projectRoot, string? editorData)
    {
        var folders = new List<string> { Path.Combine(projectRoot, "Library", "ScriptAssemblies") };
        if (editorData != null)
        {
            folders.Add(Path.Combine(editorData, "Managed", "UnityEngine"));
            folders.Add(Path.Combine(editorData, "Managed"));
            string mono = Path.Combine(editorData, "MonoBleedingEdge", "lib", "mono");
            if (Directory.Exists(mono) && Directory.EnumerateDirectories(mono, "unityjit-*").Order(StringComparer.Ordinal).FirstOrDefault() is string runtime)
            {
                folders.Add(runtime);
                folders.Add(Path.Combine(runtime, "Facades"));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (string folder in folders.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.dll").Order(StringComparer.Ordinal))
            {
                if (!seen.Contains(Path.GetFileName(file)) && IsManaged(file))
                {
                    seen.Add(Path.GetFileName(file));
                    files.Add(file);
                }
            }
        }

        return files;
    }

    /// <summary>
    /// The editor's data folder (`.../Editor/Data`, or `.../Unity.app/Contents` on macOS): beside
    /// <paramref name="executable"/> when given, else the project's version under each of
    /// <paramref name="hubFolders"/>; null when none exists.
    /// </summary>
    public static string? EditorData(string projectRoot, string? executable, IEnumerable<string> hubFolders)
    {
        if (executable != null && Path.GetDirectoryName(executable) is string folder)
        {
            string data = Path.GetFileName(folder) == "MacOS" ? Path.GetDirectoryName(folder)! : Path.Combine(folder, "Data");
            if (Directory.Exists(Path.Combine(data, "Managed")))
            {
                return data;
            }
        }

        if (UnityVersion(projectRoot) is not string version)
        {
            return null;
        }

        foreach (string hub in hubFolders)
        {
            foreach (string data in new[] { Path.Combine(hub, version, "Editor", "Data"), Path.Combine(hub, version, "Unity.app", "Contents") })
            {
                if (Directory.Exists(Path.Combine(data, "Managed")))
                {
                    return data;
                }
            }
        }

        return null;
    }

    /// <summary>Unity Hub's install folders: the one set in Hub (`secondaryInstallPath.json`), then Hub's default.</summary>
    public static IEnumerable<string> HubFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string settings = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "UnityHub")
            : Path.Combine(home, ".config", "UnityHub");
        if (SecondaryInstallPath(Path.Combine(settings, "secondaryInstallPath.json")) is string set)
        {
            yield return set;
        }

        yield return OperatingSystem.IsWindows() ? @"C:\Program Files\Unity\Hub\Editor"
            : OperatingSystem.IsMacOS() ? "/Applications/Unity/Hub/Editor"
            : Path.Combine(home, "Unity", "Hub", "Editor");
    }

    /// <summary>The folder in Hub's `secondaryInstallPath.json` (a JSON string), or null.</summary>
    public static string? SecondaryInstallPath(string file)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.String && doc.RootElement.GetString() is { Length: > 0 } path ? path : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>`m_EditorVersion` from `ProjectSettings/ProjectVersion.txt`, or null.</summary>
    public static string? UnityVersion(string projectRoot)
    {
        string file = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(file))
        {
            return null;
        }

        Match match = VersionLine().Match(File.ReadAllText(file));
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The editor scripts' defines from the generated `Assembly-CSharp-Editor.csproj`, else `UNITY_EDITOR`.</summary>
    public static IReadOnlyList<string> Defines(string projectRoot)
    {
        string csproj = Path.Combine(projectRoot, "Assembly-CSharp-Editor.csproj");
        if (File.Exists(csproj) && DefineConstants().Match(File.ReadAllText(csproj)) is { Success: true } match)
        {
            return [.. match.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        return ["UNITY_EDITOR"];
    }

    private static bool IsManaged(string file)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            return pe.HasMetadata;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"m_EditorVersion:\s*(\S+)")]
    private static partial Regex VersionLine();

    [GeneratedRegex(@"<DefineConstants>([^<]*)</DefineConstants>")]
    private static partial Regex DefineConstants();
}
