using System.Diagnostics;
using UnityGraph.Core.Graph;

namespace UnityGraph.Code;

/// <summary>
/// Writes a <see cref="CodeWorkspace"/> into the graph (SPEC-code-index, Incremental reindex):
/// everything through a bulk writer, or only what changed since the last run.
/// </summary>
public static class CodeIndex
{
    // Edges that name a key; when the key goes, the files holding them are reindexed.
    private static readonly EdgeKindSet _dependentKinds = EdgeKindSet.Of(
        EdgeKind.Calls, EdgeKind.UsesType, EdgeKind.Overrides, EdgeKind.Inherits, EdgeKind.Implements, EdgeKind.Dispatches);

    /// <summary>Adds every tracked file to <paramref name="writer"/>; the caller commits.</summary>
    public static IndexSummary Full(CodeWorkspace workspace, BulkWriter writer)
    {
        var clock = Stopwatch.StartNew();
        var problems = new List<string>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        int nodes = 0, edges = 0, vendored = 0;
        IReadOnlyList<FileContribution> files = workspace.IndexMany();
        foreach (FileContribution file in files)
        {
            var kept = new List<Node>(file.Nodes.Count);
            foreach (Node node in file.Nodes)
            {
                if (owners.TryAdd(node.Key, file.File.Path))
                {
                    kept.Add(node);
                }
                else
                {
                    problems.Add(Conflict(file.File.Path, node.Key, owners[node.Key]));
                }
            }

            writer.Add(file.File, kept, file.Edges);
            (nodes, edges) = (nodes + kept.Count, edges + file.Edges.Count);
            vendored += workspace.IsVendored(file.File.Path) ? 1 : 0;
        }

        return new IndexSummary(files.Count, vendored, nodes, edges, 0, clock.Elapsed, [.. workspace.Problems, .. problems]);
    }

    /// <summary>
    /// Brings <paramref name="store"/> up to date: reindexes changed and added files (and
    /// <paramref name="extra"/>, e.g. files a hook just edited), removes deleted ones, and
    /// reindexes files whose edges pointed at keys that went away. A new store gets a full index.
    /// <paramref name="dirty"/> is the store's dirty list when another indexer took it first;
    /// null takes it here.
    /// </summary>
    public static IndexSummary Update(CodeWorkspace workspace, GraphStore store, IEnumerable<string>? extra = null, IReadOnlyList<string>? dirty = null)
    {
        if (store.Created)
        {
            using BulkWriter writer = store.BeginBulk();
            IndexSummary full = Full(workspace, writer);
            writer.Commit();
            return full;
        }

        var clock = Stopwatch.StartNew();
        var problems = new List<string>();
        var stats = workspace.TrackedFiles.Select(f => FileStat.Of(workspace.ProjectRoot, f)).OfType<FileStat>().ToList();
        FreshnessReport report = store.CheckFreshness(stats, Indexer.Code);
        if (report.Touched.Count > 0)
        {
            store.UpdateStats(report.Touched);
        }

        var reindex = new SortedSet<string>(report.Changed.Concat(report.Added), StringComparer.Ordinal);
        reindex.UnionWith((dirty ?? store.TakeDirty()).Concat(extra ?? []).Where(workspace.IsTracked));

        var lost = new HashSet<string>(StringComparer.Ordinal);
        foreach (string removed in report.Removed)
        {
            lost.UnionWith(store.KeysInFile(removed));
            store.RemoveFile(removed);
        }

        List<FileContribution> files = [.. workspace.IndexMany(reindex)];
        foreach (FileContribution file in files)
        {
            var now = file.Nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
            lost.UnionWith(store.KeysInFile(file.File.Path).Where(k => !now.Contains(k)));
        }

        if (lost.Count > 0)
        {
            string[] dependents = store.FilesWithEdgesTo(lost, _dependentKinds)
                .Where(f => workspace.IsTracked(f) && !reindex.Contains(f))
                .ToArray();
            files.AddRange(workspace.IndexMany(dependents));
            files.Sort((a, b) => string.CompareOrdinal(a.File.Path, b.File.Path));
        }

        (int nodes, int edges) = Write(store, files, problems);
        return new IndexSummary(
            files.Count,
            files.Count(f => workspace.IsVendored(f.File.Path)),
            nodes,
            edges,
            report.Removed.Count,
            clock.Elapsed,
            [.. workspace.Problems, .. problems]);
    }

    // A key two files declare (the same type in two assemblies, or a member that moved between
    // files that are both being rewritten) stays with its current owner. A file that gave way to
    // a file later in this batch is written again once that file has had its turn.
    private static (int Nodes, int Edges) Write(GraphStore store, List<FileContribution> files, List<string> problems)
    {
        var batch = files.Select(f => f.File.Path).ToHashSet(StringComparer.Ordinal);
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);
        var retry = new List<FileContribution>();
        int nodes = 0, edges = 0;
        foreach (FileContribution file in files)
        {
            string path = file.File.Path;
            var kept = new List<Node>(file.Nodes.Count);
            bool deferred = false;
            foreach (Node node in file.Nodes)
            {
                string? owner = claimed.GetValueOrDefault(node.Key);
                if (owner is null && store.GetNode(node.Key)?.File is { } stored && stored != path)
                {
                    if (batch.Contains(stored))
                    {
                        deferred = true;
                        continue;
                    }

                    owner = stored;
                }

                if (owner is null)
                {
                    kept.Add(node);
                    claimed[node.Key] = path;
                }
                else
                {
                    problems.Add(Conflict(path, node.Key, owner));
                }
            }

            store.ReplaceFile(file.File, kept, file.Edges);
            (nodes, edges) = (nodes + kept.Count, edges + file.Edges.Count);
            if (deferred)
            {
                retry.Add(file);
            }
        }

        foreach (FileContribution file in retry)
        {
            string path = file.File.Path;
            var kept = new List<Node>(file.Nodes.Count);
            foreach (Node node in file.Nodes)
            {
                if (store.GetNode(node.Key)?.File is { } owner && owner != path)
                {
                    problems.Add(Conflict(path, node.Key, owner));
                }
                else
                {
                    kept.Add(node);
                }
            }

            nodes += kept.Count - store.KeysInFile(path).Count;
            store.ReplaceFile(file.File, kept, file.Edges);
        }

        return (nodes, edges);
    }

    private static string Conflict(string path, string key, string owner) => $"{path}: {key} is also declared in {owner}; kept there";
}
