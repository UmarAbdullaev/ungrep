using Ungrep.Core;
using Ungrep.Core.Graph;

namespace Ungrep.Assets;

/// <summary>
/// The files asset-index tracks (SPEC-asset-index, Which files are indexed), found in one walk of
/// `Assets/` and the folders in `Packages/`: every `.meta` of a file, and own Unity YAML content.
/// </summary>
public sealed class AssetWorkspace
{
    private readonly string _root;
    private readonly VendoredFolders _vendored;
    private readonly HashSet<string> _tracked;
    private readonly List<string> _problems;
    private readonly Lazy<Dictionary<string, List<string>>> _resources;

    private AssetWorkspace(string root, VendoredFolders vendored, List<FileStat> files, List<string> problems)
    {
        _root = root;
        _vendored = vendored;
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        TrackedFiles = files;
        _tracked = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        _problems = problems;
        _resources = new Lazy<Dictionary<string, List<string>>>(ResourceIndex);
    }

    public string ProjectRoot => _root;

    /// <summary>Tracked files with their size and mtime from the walk, sorted by path.</summary>
    public IReadOnlyList<FileStat> TrackedFiles { get; }

    /// <summary>Walk problems, then the problems of every file indexed so far, in path order per batch.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_problems)
            {
                return [.. _problems];
            }
        }
    }

    public bool IsTracked(string path) => _tracked.Contains(path);

    public bool IsVendored(string path) => _vendored.Contains(path);

    /// <summary>Walks the project once.</summary>
    public static AssetWorkspace Scan(string projectRoot, VendoredFolders vendored)
    {
        var files = new List<FileStat>();
        var problems = new List<string>();
        var walk = new Walk(projectRoot, vendored, files, problems);
        walk.Folder(new DirectoryInfo(Path.Combine(projectRoot, "Assets")), "Assets/");
        var packages = new DirectoryInfo(Path.Combine(projectRoot, "Packages"));
        if (packages.Exists)
        {
            foreach (DirectoryInfo package in walk.Folders(packages).OrderBy(d => d.Name, StringComparer.Ordinal))
            {
                walk.Folder(package, "Packages/" + package.Name + "/");
            }
        }

        return new AssetWorkspace(projectRoot, vendored, files, problems);
    }

    /// <summary>Indexes <paramref name="paths"/> (every tracked file when null), in parallel; results in path order.</summary>
    /// <exception cref="ArgumentException">A path is not tracked.</exception>
    public IReadOnlyList<FileContribution> IndexMany(IEnumerable<string>? paths = null)
    {
        List<string> files = (paths ?? TrackedFiles.Select(f => f.Path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (string file in files.Where(f => !_tracked.Contains(f)))
        {
            throw new ArgumentException("not a tracked asset file: " + file, nameof(paths));
        }

        var results = new FileContribution[files.Count];
        var problems = new List<string>[files.Count];
        Parallel.For(0, files.Count, i =>
        {
            problems[i] = [];
            results[i] = files[i].EndsWith(".meta", StringComparison.Ordinal)
                ? AssetFileIndexer.Meta(_root, files[i], problems[i])
                : AssetFileIndexer.Content(_root, files[i], problems[i], Resources);
        });

        lock (_problems)
        {
            _problems.AddRange(problems.SelectMany(p => p));
        }

        return results;
    }

    /// <summary>The assets `Resources.Load(name)` could load: under a `Resources/` folder at <paramref name="name"/>, without extension.</summary>
    public IReadOnlyList<string> Resources(string name) => _resources.Value.TryGetValue(name, out List<string>? found) ? found : [];

    private Dictionary<string, List<string>> ResourceIndex()
    {
        const string Folder = "/Resources/";
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (FileStat file in TrackedFiles.Where(f => f.Path.EndsWith(".meta", StringComparison.Ordinal)))
        {
            string asset = file.Path[..^".meta".Length];
            for (int at = asset.IndexOf(Folder, StringComparison.Ordinal); at >= 0; at = asset.IndexOf(Folder, at + 1, StringComparison.Ordinal))
            {
                string below = asset[(at + Folder.Length)..];
                string dot = Path.GetExtension(below);
                string name = below[..^dot.Length];
                if (!index.TryGetValue(name, out List<string>? paths))
                {
                    index[name] = paths = [];
                }

                paths.Add(asset);
            }
        }

        return index;
    }

    private sealed class Walk(string root, VendoredFolders vendored, List<FileStat> files, List<string> problems)
    {
        public void Folder(DirectoryInfo folder, string prefix)
        {
            if (!folder.Exists)
            {
                return;
            }

            FileSystemInfo[] entries;
            try
            {
                entries = folder.GetFileSystemInfos();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{prefix}: {e.Message}");
                return;
            }

            var folders = new HashSet<string>(StringComparer.Ordinal);
            foreach (FileSystemInfo entry in entries)
            {
                if (entry is DirectoryInfo)
                {
                    folders.Add(entry.Name);
                }
            }

            foreach (FileSystemInfo entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry is DirectoryInfo sub)
                {
                    if (!Skipped(sub))
                    {
                        Folder(sub, prefix + sub.Name + "/");
                    }

                    continue;
                }

                string path = prefix + entry.Name;
                bool tracked = entry.Name.EndsWith(".meta", StringComparison.Ordinal)
                    ? !folders.Contains(entry.Name[..^".meta".Length])
                    : AssetTypes.IsContent(path) && !vendored.Contains(path);
                if (tracked && entry is FileInfo file)
                {
                    files.Add(new FileStat(path, file.Length, FileStat.MtimeNsOf(file)));
                }
            }
        }

        public IEnumerable<DirectoryInfo> Folders(DirectoryInfo folder)
        {
            try
            {
                return folder.GetDirectories().Where(d => !Skipped(d));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetRelativePath(root, folder.FullName).Replace('\\', '/')}/: {e.Message}");
                return [];
            }
        }

        // Unity skips hidden folders (`.git`) and `Name~` folders (`Samples~`); links are not
        // followed, so a link cycle cannot loop the walk.
        private static bool Skipped(DirectoryInfo folder) =>
            folder.Name.StartsWith('.') || folder.Name.EndsWith('~') || (folder.Attributes & FileAttributes.ReparsePoint) != 0;
    }
}
