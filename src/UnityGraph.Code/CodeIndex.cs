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
        IReadOnlyList<FileContribution> files = workspace.IndexMany();
        (int nodes, int edges) = Contributions.AddAll(writer, files, problems);
        int vendored = files.Count(f => workspace.IsVendored(f.File.Path));
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

        (int nodes, int edges) = Contributions.Write(store, files, problems);
        return new IndexSummary(
            files.Count,
            files.Count(f => workspace.IsVendored(f.File.Path)),
            nodes,
            edges,
            report.Removed.Count,
            clock.Elapsed,
            [.. workspace.Problems, .. problems]);
    }
}
