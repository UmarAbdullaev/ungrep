using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Ungrep.Core;
using Ungrep.Core.Graph;

namespace Ungrep.Code;

/// <summary>
/// A Unity project's C#, loaded from its generated `.csproj` files (SPEC-code-index). Knows which
/// files are indexed, builds one Roslyn compilation per project on demand (project references
/// compiled from source, as compilation references) and turns files into graph rows.
/// </summary>
public sealed class CodeWorkspace
{
    private static readonly string[] _bclNames = ["mscorlib.dll", "netstandard.dll", "System.Runtime.dll"];

    private readonly string _root;
    private readonly VendoredFolders _vendored;
    private readonly Dictionary<string, UnityCsproj> _projects;
    private readonly Dictionary<string, string> _projectOfFile;
    private readonly HashSet<string> _tracked;
    private readonly List<string> _problems;
    private readonly Dictionary<string, MetadataReference> _metadata = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _building = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Compilation, string), IReadOnlyList<IMethodSymbol>> _messageTargets = new();
    private readonly object _gate = new();
    private IReadOnlyList<MetadataReference>? _runtimeBcl;

    private CodeWorkspace(string root, VendoredFolders vendored, Dictionary<string, UnityCsproj> projects, Dictionary<string, string> projectOfFile, List<string> problems)
    {
        _root = root;
        _vendored = vendored;
        _projects = projects;
        _projectOfFile = projectOfFile;
        _problems = problems;
        _tracked = new HashSet<string>(projectOfFile.Keys, StringComparer.Ordinal);
        TrackedFiles = projectOfFile.Keys.Order(StringComparer.Ordinal).ToList();
    }

    public string ProjectRoot => _root;

    /// <summary>Indexed `.cs` files that exist on disk, ordered.</summary>
    public IReadOnlyList<string> TrackedFiles { get; }

    /// <summary>False when the project has no `.csproj` files: Unity hasn't generated them.</summary>
    public bool HasProjects => _projects.Count > 0;

    /// <summary>Missing references, unreadable projects, reference cycles; ordered, no duplicates.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_gate)
            {
                return _problems.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            }
        }
    }

    public bool IsVendored(string path) => _vendored.Contains(path);

    public bool IsTracked(string path) => _tracked.Contains(path);

    public static CodeWorkspace Load(string projectRoot, VendoredFolders vendored)
    {
        string root = Path.GetFullPath(projectRoot);
        var problems = new List<string>();
        var projects = UnityCsproj.LoadAll(root, problems).ToDictionary(p => p.Path, StringComparer.Ordinal);

        // Compile items first (a file listed twice belongs to the first project by path), then
        // files on disk that no project lists yet: Unity adds them on its next refresh.
        var projectOfFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (UnityCsproj project in projects.Values.OrderBy(p => p.Path, StringComparer.Ordinal))
        {
            foreach (string file in project.Compile.Where(IsIndexable))
            {
                if (File.Exists(Path.Combine(root, file)))
                {
                    projectOfFile.TryAdd(file, project.Path);
                }
            }
        }

        if (projects.Count > 0)
        {
            var folders = projectOfFile
                .GroupBy(f => f.Value, f => Folder(f.Key), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            foreach (string file in SourceFilesOnDisk(root).Where(f => !projectOfFile.ContainsKey(f)))
            {
                projectOfFile[file] = ClosestProject(file, folders, projects);
            }
        }

        return new CodeWorkspace(root, vendored, projects, projectOfFile, problems);
    }

    /// <summary>Only files under `Assets/` and embedded packages are indexed; `Library/` is compiled, never indexed.</summary>
    internal static bool IsIndexable(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        && (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal));

    private static string Folder(string path) => path[..(path.LastIndexOf('/') + 1)];

    // The project whose files share the longest folder prefix; Assembly-CSharp when none does.
    private static string ClosestProject(string file, Dictionary<string, List<string>> folders, Dictionary<string, UnityCsproj> projects)
    {
        string? best = null;
        int bestLength = 0;
        foreach (var (project, dirs) in folders.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            foreach (string dir in dirs)
            {
                int length = CommonFolderLength(file, dir);
                if (length > bestLength)
                {
                    (best, bestLength) = (project, length);
                }
            }
        }

        // Only a shared folder below Assets/ or Packages/<name>/ counts as a match.
        return best is not null && bestLength > file.IndexOf('/') + 1
            ? best
            : projects.ContainsKey("Assembly-CSharp.csproj") ? "Assembly-CSharp.csproj" : projects.Keys.Order(StringComparer.Ordinal).First();
    }

    private static int CommonFolderLength(string file, string dir)
    {
        int length = 0;
        for (int i = 0; i < Math.Min(file.Length, dir.Length) && file[i] == dir[i]; i++)
        {
            if (dir[i] == '/')
            {
                length = i + 1;
            }
        }

        return length;
    }

    // Unity skips folders starting with `.` or ending in `~` (e.g. `Samples~`).
    private static IEnumerable<string> SourceFilesOnDisk(string root)
    {
        var dirs = new List<string>();
        if (Directory.Exists(Path.Combine(root, "Assets")))
        {
            dirs.Add(Path.Combine(root, "Assets"));
        }

        if (Directory.Exists(Path.Combine(root, "Packages")))
        {
            dirs.AddRange(Directory.EnumerateDirectories(Path.Combine(root, "Packages")));
        }

        var stack = new Stack<string>(dirs);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.cs").ToList();
                subdirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return Path.GetRelativePath(root, file).Replace('\\', '/');
            }

            foreach (string sub in subdirs)
            {
                string name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && !name.EndsWith('~'))
                {
                    stack.Push(sub);
                }
            }
        }
    }

    /// <summary>The `.csproj` a tracked file compiles in, or null.</summary>
    internal string? ProjectOf(string path) => _projectOfFile.GetValueOrDefault(path);

    /// <summary>Compiler errors located in indexed files, for tests and `ug doctor`.</summary>
    internal IReadOnlyList<Diagnostic> SourceErrors()
    {
        var errors = new List<Diagnostic>();
        foreach (string project in _projects.Keys.Order(StringComparer.Ordinal))
        {
            errors.AddRange(Load(project).Compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error && d.Location.SourceTree is { } tree && _tracked.Contains(tree.FilePath)));
        }

        return errors;
    }

    /// <summary>The bound model of a tracked file, for measurements in tests.</summary>
    internal SemanticModel? ModelOf(string path) =>
        _projectOfFile.TryGetValue(path, out string? project) && Load(project) is var loaded
            && loaded.Sources.TryGetValue(path, out Source? source) && source.Tree is { } tree
            ? loaded.Compilation.GetSemanticModel(tree)
            : null;

    /// <summary>Indexes one tracked file.</summary>
    /// <exception cref="ArgumentException">Not a tracked file.</exception>
    public FileContribution Index(string path)
    {
        if (!_projectOfFile.TryGetValue(path, out string? project))
        {
            throw new ArgumentException("not a tracked C# file: " + path, nameof(path));
        }

        return IndexIn(Load(project), path);
    }

    /// <summary>Indexes <paramref name="paths"/> (every tracked file when null), in parallel; results in path order.</summary>
    public IReadOnlyList<FileContribution> IndexMany(IEnumerable<string>? paths = null)
    {
        List<string> files = (paths ?? TrackedFiles).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (string file in files.Where(f => !_projectOfFile.ContainsKey(f)))
        {
            throw new ArgumentException("not a tracked C# file: " + file, nameof(paths));
        }

        // Build every needed compilation first; the walk below only reads them.
        foreach (string project in files.Select(f => _projectOfFile[f]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            Load(project);
        }

        var results = new FileContribution[files.Count];
        Parallel.For(0, files.Count, i => results[i] = IndexIn(Load(_projectOfFile[files[i]]), files[i]));
        return results;
    }

    private FileContribution IndexIn(Loaded project, string path)
    {
        if (!project.Sources.TryGetValue(path, out Source? source) || source.Tree is null)
        {
            return new FileContribution(source?.Record ?? ErrorRecord(path), [], []);
        }

        SemanticModel model = project.Compilation.GetSemanticModel(source.Tree);
        var indexer = new FileIndexer(this, model, source.Tree, path, declarationsOnly: IsVendored(path), ScriptGuid(path));
        var (nodes, edges) = indexer.Run();
        return new FileContribution(source.Record, nodes, edges);
    }

    // Targets of SendMessage("name"): methods of that name on MonoBehaviours this compilation can see.
    internal IReadOnlyList<IMethodSymbol> MessageTargets(Compilation compilation, string name) =>
        _messageTargets.GetOrAdd((compilation, name), key =>
        {
            var found = new List<IMethodSymbol>();
            IEnumerable<Compilation> visible = [key.Item1, .. key.Item1.References.OfType<CompilationReference>().Select(r => r.Compilation)];
            foreach (Compilation c in visible)
            {
                found.AddRange(c.GetSymbolsWithName(key.Item2, SymbolFilter.Member).OfType<IMethodSymbol>()
                    .Where(m => m.MethodKind == MethodKind.Ordinary && UnityRules.KindOf(m.ContainingType) == "mono_behaviour" && IsIndexedSource(m)));
            }

            return found;
        });

    /// <summary>True when <paramref name="symbol"/> is declared in an indexed file (so its node exists).</summary>
    internal bool IsIndexedSource(ISymbol symbol) =>
        symbol.Locations.Any(l => l.IsInSource && l.SourceTree is { } tree && _tracked.Contains(tree.FilePath));

    private string? ScriptGuid(string path)
    {
        try
        {
            string meta = Path.Combine(_root, path + ".meta");
            return File.Exists(meta) ? MetaGuid(File.ReadAllText(meta)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The `guid:` of a `.meta` file: 32 lowercase hex characters, or null.</summary>
    internal static string? MetaGuid(string meta)
    {
        foreach (string line in meta.Split('\n'))
        {
            if (line.StartsWith("guid:", StringComparison.Ordinal))
            {
                string guid = line[5..].Trim();
                return guid.Length == 32 && guid.All(char.IsAsciiHexDigitLower) ? guid : null;
            }
        }

        return null;
    }

    // Loads a project's compilation and, first, those it references. Not thread-safe by itself:
    // IndexMany loads before going parallel; Index loads on the caller's thread.
    private Loaded Load(string projectPath)
    {
        lock (_gate)
        {
            return LoadLocked(projectPath);
        }
    }

    private Loaded LoadLocked(string projectPath)
    {
        if (_loaded.TryGetValue(projectPath, out Loaded? done))
        {
            return done;
        }

        UnityCsproj project = _projects[projectPath];
        _building.Add(projectPath);
        var references = new List<MetadataReference>();
        foreach (string reference in project.References)
        {
            if (Metadata(reference) is { } metadata)
            {
                references.Add(metadata);
            }
            else
            {
                _problems.Add("missing reference " + Display(reference));
            }
        }

        if (!project.References.Any(r => _bclNames.Contains(Path.GetFileName(r), StringComparer.OrdinalIgnoreCase) && File.Exists(r)))
        {
            // The editor moved, or a fixture: bind against the running runtime; BCL doc IDs are the same.
            references.AddRange(RuntimeBcl());
            _problems.Add("no BCL reference found (mscorlib, netstandard); used the .NET runtime's assemblies");
        }

        // Unity compiles against referenced assemblies' references too, so take the closure.
        var projectReferences = new List<MetadataReference>();
        foreach (string referenced in project.ProjectReferences)
        {
            if (!_projects.ContainsKey(referenced))
            {
                _problems.Add($"{projectPath}: referenced project {referenced} not found");
            }
            else if (_building.Contains(referenced))
            {
                _problems.Add($"{projectPath}: reference cycle through {referenced}, reference dropped");
            }
            else if (PackageAssembly(_projects[referenced]) is { } dll)
            {
                projectReferences.Add(dll);
            }
            else
            {
                Loaded dependency = LoadLocked(referenced);
                projectReferences.Add(dependency.Self);
                projectReferences.AddRange(dependency.ProjectReferences);
            }
        }

        projectReferences = projectReferences.Distinct().ToList();
        references.AddRange(projectReferences);

        var parse = new CSharpParseOptions(
            LanguageVersionFacts.TryParse(project.LangVersion, out LanguageVersion version) ? version : LanguageVersion.CSharp9,
            DocumentationMode.None,
            SourceCodeKind.Regular,
            project.Defines);
        List<string> files = project.Compile
            .Concat(_projectOfFile.Where(f => f.Value == projectPath).Select(f => f.Key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var sources = new Source[files.Count];
        Parallel.For(0, files.Count, i => sources[i] = Read(files[i], parse));

        var compilation = CSharpCompilation.Create(
            project.AssemblyName,
            sources.Where(s => s.Tree is not null).Select(s => s.Tree!),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: project.AllowUnsafe, concurrentBuild: true));
        var loaded = new Loaded(compilation, compilation.ToMetadataReference(), projectReferences, sources.ToDictionary(s => s.Path, StringComparer.Ordinal));
        _building.Remove(projectPath);
        _loaded[projectPath] = loaded;
        return loaded;
    }

    // Package code can't change under Library/PackageCache, so Unity's compiled DLL of it is as
    // good as the source and much cheaper to load.
    private MetadataReference? PackageAssembly(UnityCsproj project)
    {
        if (project.Compile.Count == 0 || !project.Compile.All(f => f.StartsWith("Library/", StringComparison.Ordinal)))
        {
            return null;
        }

        return Metadata(Path.Combine(_root, "Library", "ScriptAssemblies", project.AssemblyName + ".dll"));
    }

    private MetadataReference? Metadata(string fullPath)
    {
        if (_metadata.TryGetValue(fullPath, out MetadataReference? cached))
        {
            return cached;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            MetadataReference reference = MetadataReference.CreateFromFile(fullPath);
            _metadata[fullPath] = reference;
            return reference;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
    }

    private IReadOnlyList<MetadataReference> RuntimeBcl()
    {
        if (_runtimeBcl is null)
        {
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RuntimeEnvironment.GetRuntimeDirectory()));
            _runtimeBcl = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => string.Equals(Path.GetDirectoryName(Path.GetFullPath(p)), dir, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .Select(p => Metadata(p))
                .OfType<MetadataReference>()
                .ToList();
        }

        return _runtimeBcl;
    }

    private string Display(string fullPath)
    {
        string relative = Path.GetRelativePath(_root, fullPath);
        return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? fullPath : relative).Replace('\\', '/');
    }

    // Stat before reading: if the file changes in between, the next freshness check sees a new
    // mtime, hashes it and finds the change.
    private Source Read(string path, CSharpParseOptions parse)
    {
        string full = Path.Combine(_root, path);
        FileStat? stat = FileStat.Of(_root, path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Source(path, ErrorRecord(path, stat), null);
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var record = new FileRecord(path, stat?.Size ?? bytes.Length, stat?.MtimeNs ?? 0, hash, Indexer.Code);
        SourceText text = SourceText.From(bytes, bytes.Length, throwIfBinaryDetected: false, canBeEmbedded: false);
        return new Source(path, record, CSharpSyntaxTree.ParseText(text, parse, path));
    }

    private static FileRecord ErrorRecord(string path, FileStat? stat = null) =>
        new(path, stat?.Size ?? 0, stat?.MtimeNs ?? 0, Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant(), Indexer.Code, FileStatus.Error);

    private sealed record Source(string Path, FileRecord Record, SyntaxTree? Tree);

    private sealed record Loaded(
        CSharpCompilation Compilation,
        MetadataReference Self,
        IReadOnlyList<MetadataReference> ProjectReferences,
        Dictionary<string, Source> Sources);
}
