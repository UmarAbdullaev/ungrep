using UnityGraph.Assets;
using UnityGraph.Code;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Cli;

/// <summary>
/// Runs both indexers over one store (SPEC-asset-index, Running with code-index): code first,
/// then assets. A new store, or <see cref="Full"/>, indexes both through one bulk writer.
/// </summary>
internal sealed class GraphUpdate
{
    public const string NoProjects = "no .csproj files: in Unity, Preferences > External Tools > Regenerate project files";

    private GraphUpdate(CodeWorkspace? code, AssetWorkspace? assets, IReadOnlyList<string> warnings)
    {
        Code = code;
        Assets = assets;
        Warnings = warnings;
    }

    /// <summary>The code workspace; null when code-index is off or there are no `.csproj` files.</summary>
    public CodeWorkspace? Code { get; }

    /// <summary>The asset workspace; null when asset-index is off.</summary>
    public AssetWorkspace? Assets { get; }

    /// <summary>Why an indexer that is on was skipped.</summary>
    public IReadOnlyList<string> Warnings { get; }

    public bool IsEmpty => Code is null && Assets is null;

    public static GraphUpdate Load(string root, UnityGraphConfig config)
    {
        CodeWorkspace? code = null;
        var warnings = new List<string>();
        if (config.IsEnabled("code-index"))
        {
            code = CodeWorkspace.Load(root, config.Vendored);
            if (!code.HasProjects)
            {
                code = null;
                warnings.Add(NoProjects);
            }
        }

        AssetWorkspace? assets = config.IsEnabled("asset-index") ? AssetWorkspace.Scan(root, config.Vendored) : null;
        return new GraphUpdate(code, assets, warnings);
    }

    /// <summary>Adds everything to <paramref name="writer"/>; the caller commits.</summary>
    public IndexSummary Full(BulkWriter writer)
    {
        IndexSummary summary = IndexSummary.Empty with { Problems = Warnings };
        if (Code is not null)
        {
            summary = summary.Plus(CodeIndex.Full(Code, writer));
        }

        if (Assets is not null)
        {
            summary = summary.Plus(AssetIndex.Full(Assets, writer));
        }

        return summary;
    }

    /// <summary>Brings <paramref name="store"/> up to date; a new store gets <see cref="Full"/>.</summary>
    public IndexSummary Update(GraphStore store, IEnumerable<string>? extra = null)
    {
        if (store.Created)
        {
            using BulkWriter writer = store.BeginBulk();
            IndexSummary full = Full(writer);
            writer.Commit();
            return full;
        }

        IReadOnlyList<string> dirty = store.TakeDirty();
        string[] edited = extra?.ToArray() ?? [];
        IndexSummary summary = IndexSummary.Empty with { Problems = Warnings };
        if (Code is not null)
        {
            summary = summary.Plus(CodeIndex.Update(Code, store, edited, dirty));
        }

        if (Assets is not null)
        {
            summary = summary.Plus(AssetIndex.Update(Assets, store, dirty, edited));
        }

        return summary;
    }
}
