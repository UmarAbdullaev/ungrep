using System.Text.Json;
using UnityGraph.Core;
using UnityGraph.Core.Graph;
using UnityGraph.Query;

namespace UnityGraph.Hooks;

/// <summary>
/// A shell move, copy or remove with its paths made project-relative (`/`); a path outside the
/// project is null. Ops whose relative paths follow a `cd` that can't be followed are left out.
/// </summary>
internal sealed record ShellOp(FileOpKind Kind, string Verb, IReadOnlyList<string?> Sources, string? Destination, bool Recursive);

/// <summary>
/// What the guards of one PreToolUse call share: the project, its config, the call's paths and
/// shell operations, and the graph, opened read-only on first use (SPEC-guards, shared rule 1).
/// </summary>
internal sealed class GuardContext : IDisposable
{
    private readonly string? _graphDirectory;
    private GraphStore? _store;
    private QueryEngine? _graph;
    private bool _opened;
    private IReadOnlyList<ShellOp>? _shellOps;

    // graphDirectory: the graph's folder when it is not the project's own (a replay keeps it in a temp folder).
    public GuardContext(HookInput input, UnityProject project, UnityGraphConfig config, string? graphDirectory = null)
    {
        Input = input;
        Root = Path.GetFullPath(project.Root);
        Config = config;
        Cwd = input.Cwd is { Length: > 0 } cwd ? Path.GetFullPath(Native(cwd), Root) : Root;
        _graphDirectory = graphDirectory;
    }

    public HookInput Input { get; }

    public string Root { get; }

    /// <summary>The session's working folder: relative paths in the call resolve against it.</summary>
    public string Cwd { get; }

    public UnityGraphConfig Config { get; }

    /// <summary>The graph, or null when the project has never been indexed.</summary>
    public QueryEngine? Graph
    {
        get
        {
            if (!_opened)
            {
                _opened = true;
                try
                {
                    _store = GraphStore.Open(Root, StoreMode.ReadOnly, graphDirectory: _graphDirectory);
                    _graph = new QueryEngine(_store, Root);
                }
                catch (GraphRebuildRequiredException)
                {
                    // Silent until the first index (shared rule 2).
                }
            }

            return _graph;
        }
    }

    /// <summary>A string argument of the tool call, or null.</summary>
    public string? Argument(string name) =>
        Input.ToolInput.ValueKind == JsonValueKind.Object && Input.ToolInput.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// <paramref name="path"/> resolved against <paramref name="baseDirectory"/> (the session's
    /// folder by default) as a project path without a trailing `/`; null outside the project.
    /// </summary>
    public string? ProjectPath(string path, string? baseDirectory = null)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Native(path), baseDirectory ?? Cwd);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string relative = Path.GetRelativePath(Root, full).Replace('\\', '/').TrimEnd('/');
        return relative == "." ? ""
            : relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null
            : relative;
    }

    /// <summary>Under `Assets/` or `Packages/`, where Unity imports files and keeps `.meta` files.</summary>
    public static bool InAssets(string? path) =>
        path is not null && (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal));

    public string FullPath(string projectPath) => Path.Combine(Root, projectPath);

    /// <summary>The project path of the edited file when it is a script of the project's own code (not vendored); else null.</summary>
    public string? OwnScript(string? filePath) =>
        filePath is not null && ProjectPath(filePath) is { } path && InAssets(path)
            && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !Config.Vendored.Contains(path) ? path : null;

    /// <summary>Whether the file is as the graph indexed it: same size, and same mtime or same content.</summary>
    public bool IsFresh(string projectPath)
    {
        if (Graph is null || _store!.GetFile(projectPath) is not { } record || FileStat.Of(Root, projectPath) is not { } stat || stat.Size != record.Size)
        {
            return false;
        }

        return stat.MtimeNs == record.MtimeNs || FileHash.Of(FullPath(projectPath)) == record.Hash;
    }

    /// <summary>The asset node of a GUID: its project path, or null when the graph has none.</summary>
    public string? AssetPathOf(string guid) =>
        Graph is null || _store!.GetNode(NodeKeys.Asset(guid)) is not { } asset ? null
        : asset.File.EndsWith(".meta", StringComparison.Ordinal) ? asset.File[..^".meta".Length] : asset.File;

    /// <summary>Indexed files under a project folder.</summary>
    public IReadOnlyList<string> FilesUnder(string folder) => Graph is null ? [] : _store!.FilesUnder(folder + "/");

    /// <summary>The moves, copies and removes of a Bash or PowerShell call, in order; empty for other tools.</summary>
    public IReadOnlyList<ShellOp> ShellOps() => _shellOps ??= ReadShellOps();

    public void Dispose() => _store?.Dispose();

    private List<ShellOp> ReadShellOps()
    {
        var ops = new List<ShellOp>();
        if (Input.ToolName is not ("Bash" or "PowerShell") || Argument("command") is not { } command)
        {
            return ops;
        }

        string? directory = Cwd;
        foreach (FileOp op in ShellCommand.Operations(command, Input.ToolName == "PowerShell"))
        {
            if (op.Kind == FileOpKind.ChangeDirectory)
            {
                directory = op.Destination is { } to && directory is not null ? Resolve(to, directory) : null;
                continue;
            }

            IEnumerable<string> paths = op.Destination is null ? op.Sources : op.Sources.Append(op.Destination);
            if (directory is null && paths.Any(p => !Path.IsPathRooted(Native(p))))
            {
                continue;
            }

            ops.Add(new ShellOp(op.Kind, op.Verb, [.. op.Sources.Select(s => ProjectPath(s, directory))],
                op.Destination is { } destination ? ProjectPath(destination, directory) : null, op.Recursive));
        }

        return ops;
    }

    private static string? Resolve(string path, string directory)
    {
        try
        {
            return Path.GetFullPath(Native(path), directory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Git Bash on Windows writes `/d/Projects/x` for `D:\Projects\x`.
    private static string Native(string path) =>
        OperatingSystem.IsWindows() && path.Length >= 2 && path[0] == '/' && char.IsAsciiLetter(path[1]) && (path.Length == 2 || path[2] == '/')
            ? char.ToUpperInvariant(path[1]) + ":/" + (path.Length > 3 ? path[3..] : "")
            : path;
}
