using System.Diagnostics;
using UnityGraph.Core.Graph;

namespace UnityGraph.Assets;

/// <summary>
/// Writes an <see cref="AssetWorkspace"/> into the graph (SPEC-asset-index, Incremental reindex):
/// everything through a bulk writer, or only what changed since the last run.
/// </summary>
public static class AssetIndex
{
    /// <summary>Adds every tracked file to <paramref name="writer"/>; the caller commits.</summary>
    public static IndexSummary Full(AssetWorkspace workspace, BulkWriter writer)
    {
        var clock = Stopwatch.StartNew();
        var problems = new List<string>();
        IReadOnlyList<FileContribution> files = workspace.IndexMany();
        (int nodes, int edges) = Contributions.AddAll(writer, files, problems);
        int vendored = files.Count(f => workspace.IsVendored(f.File.Path));
        return new IndexSummary(files.Count, vendored, nodes, edges, 0, clock.Elapsed, [.. workspace.Problems, .. problems]);
    }

    /// <summary>
    /// Brings <paramref name="store"/> up to date: reindexes changed and added files, the files in
    /// <paramref name="dirty"/> and <paramref name="extra"/> that asset-index tracks, and the
    /// content file of every changed, added or removed `.meta`; removes deleted files. Edges into
    /// objects that went away stay, and read as unresolved.
    /// </summary>
    public static IndexSummary Update(AssetWorkspace workspace, GraphStore store, IReadOnlyList<string> dirty, IEnumerable<string>? extra = null)
    {
        var clock = Stopwatch.StartNew();
        var problems = new List<string>();
        FreshnessReport report = store.CheckFreshness(workspace.TrackedFiles, Indexer.Asset);
        if (report.Touched.Count > 0)
        {
            store.UpdateStats(report.Touched);
        }

        var reindex = new SortedSet<string>(report.Changed.Concat(report.Added), StringComparer.Ordinal);
        reindex.UnionWith(dirty.Concat(extra ?? []).Where(workspace.IsTracked));
        foreach (string meta in report.Changed.Concat(report.Added).Concat(report.Removed).Where(p => p.EndsWith(".meta", StringComparison.Ordinal)))
        {
            string content = meta[..^".meta".Length];
            if (workspace.IsTracked(content))
            {
                reindex.Add(content);
            }
        }

        foreach (string removed in report.Removed)
        {
            store.RemoveFile(removed);
        }

        List<FileContribution> files = [.. workspace.IndexMany(reindex)];
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
