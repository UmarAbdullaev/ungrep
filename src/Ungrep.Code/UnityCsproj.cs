using System.Xml.Linq;

namespace Ungrep.Code;

/// <summary>
/// One of the legacy (non-SDK) `.csproj` files Unity generates, read as plain XML: everything a
/// compilation needs is spelled out in it, so no MSBuild is involved (SPEC Decisions #2).
/// Paths are project-relative with forward slashes, except references, which are full paths.
/// </summary>
public sealed record UnityCsproj(
    string Path,
    string AssemblyName,
    IReadOnlyList<string> Compile,
    IReadOnlyList<string> References,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> Defines,
    string LangVersion,
    bool AllowUnsafe)
{
    /// <summary>Parses the project at <paramref name="path"/> (project-relative).</summary>
    /// <exception cref="FormatException">Not a readable project file.</exception>
    public static UnityCsproj Parse(string xml, string projectRoot, string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException e)
        {
            throw new FormatException($"{path}: {e.Message}", e);
        }

        IEnumerable<XElement> All(string name) => doc.Descendants().Where(e => e.Name.LocalName == name);
        string? Property(string name) => All(name).Select(e => e.Value.Trim()).LastOrDefault(v => v.Length > 0);
        string? Include(XElement e) => e.Attribute("Include")?.Value is { Length: > 0 } v ? v : null;

        string assembly = Property("AssemblyName") ?? System.IO.Path.GetFileNameWithoutExtension(path);
        var compile = All("Compile").Select(Include).OfType<string>().Select(Relative).Distinct(StringComparer.Ordinal).ToList();
        var references = All("Reference")
            .Select(r => r.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value.Trim())
            .OfType<string>()
            .Where(h => h.Length > 0)
            .Select(h => System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(Slashes(h)) ? Slashes(h) : System.IO.Path.Combine(projectRoot, Slashes(h))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var projects = All("ProjectReference").Select(Include).OfType<string>().Select(Relative).Distinct(StringComparer.Ordinal).ToList();
        var defines = (Property("DefineConstants") ?? "")
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        bool unsafeCode = string.Equals(Property("AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase);
        return new UnityCsproj(path, assembly, compile, references, projects, defines, Property("LangVersion") ?? "9.0", unsafeCode);
    }

    /// <summary>Reads and parses every `*.csproj` in the project root, ordered by path.</summary>
    public static IReadOnlyList<UnityCsproj> LoadAll(string projectRoot, List<string> problems)
    {
        var projects = new List<UnityCsproj>();
        foreach (string file in Directory.EnumerateFiles(projectRoot, "*.csproj").Order(StringComparer.Ordinal))
        {
            string name = System.IO.Path.GetFileName(file);
            try
            {
                projects.Add(Parse(File.ReadAllText(file), projectRoot, name));
            }
            catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
            {
                problems.Add($"{name}: not readable ({e.Message})");
            }
        }

        return projects;
    }

    // `Assets\Scripts\A.cs` → `Assets/Scripts/A.cs`. XML escapes (`&apos;`) are already decoded.
    private static string Relative(string include)
    {
        string path = Slashes(include);
        return path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;
    }

    private static string Slashes(string path) => path.Replace('\\', '/');
}
