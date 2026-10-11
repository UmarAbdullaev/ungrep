namespace Ungrep.Hooks;

/// <summary>
/// Case 3 (`meta`): shell moves, removes and copies that break the `.meta` file pairing: an asset
/// moved without its `.meta` or a `.meta` removed alone (the asset gets a new GUID and its
/// references break), and copies of `.meta` files into the project (duplicate GUIDs).
/// </summary>
internal sealed class MetaGuard : IGuard
{
    public string Case => "meta";

    public IEnumerable<GuardNote> Check(GuardContext context)
    {
        IReadOnlyList<ShellOp> ops = context.ShellOps();
        if (!ops.Any(o => o.Sources.Any(GuardContext.InAssets)) || context.Graph is null)
        {
            yield break;
        }

        HashSet<string> moved = Sources(ops, FileOpKind.Move);
        HashSet<string> removed = Sources(ops, FileOpKind.Remove);
        foreach (ShellOp op in ops)
        {
            GuardNote? note = op.Kind switch
            {
                FileOpKind.Move when GuardContext.InAssets(op.Destination) => MovedAlone(context, op, moved),
                FileOpKind.Remove => RemovedAlone(context, op, removed, moved),
                FileOpKind.Copy when GuardContext.InAssets(op.Destination) => CopiedMetas(context, op),
                _ => null,
            };
            if (note is not null)
            {
                yield return note;
            }
        }
    }

    // a: assets moved inside the project whose referenced `.meta` stays behind.
    private GuardNote? MovedAlone(GuardContext context, ShellOp op, HashSet<string> moved)
    {
        var left = new List<(string Asset, int Referrers)>();
        foreach (string source in Assets(op).Where(s => !IsMeta(s) && !moved.Contains(s + ".meta")))
        {
            if (Referrers(context, source) is > 0 and int count)
            {
                left.Add((source, count));
            }
        }

        if (left.Count == 0)
        {
            return null;
        }

        (string asset, int referrers) = left[0];
        string destination = op.Destination!;
        bool intoFolder = op.Sources.Count > 1 || Directory.Exists(context.FullPath(destination));
        string target = (intoFolder ? destination + "/" + GuardOutput.FileName(asset) : destination) + ".meta";
        string fix = op.Verb.Equals("Rename-Item", StringComparison.OrdinalIgnoreCase) || op.Verb is "ren" or "rni"
            ? $"{op.Verb} {asset}.meta {GuardOutput.FileName(target)}"
            : $"{op.Verb} {asset}.meta {target}";
        string more = left.Count > 1 ? $" (+{left.Count - 1} more .meta {(left.Count == 2 ? "file" : "files")})" : "";
        return new GuardNote(Case, string.Join(" ", left.Select(l => l.Asset)),
            $"ungrep: this {op.Verb} leaves {asset}.meta behind{more}.\n"
            + $"  {asset} is referenced by {GuardOutput.Count(referrers, "asset")} (ug refs {GuardOutput.FileName(asset)}); at the new path Unity gives it a new GUID and they lose it.\n"
            + $"  Move the .meta with it: {fix}");
    }

    // b: a referenced asset's `.meta` removed while the asset stays.
    private GuardNote? RemovedAlone(GuardContext context, ShellOp op, HashSet<string> removed, HashSet<string> moved)
    {
        foreach (string meta in Assets(op).Where(IsMeta))
        {
            string asset = meta[..^".meta".Length];
            if (!removed.Contains(asset) && !moved.Contains(asset) && Referrers(context, asset) is > 0 and int count)
            {
                return new GuardNote(Case, meta,
                    $"ungrep: this {op.Verb} deletes {meta} but keeps the asset.\n"
                    + $"  {asset} is referenced by {GuardOutput.Count(count, "asset")} (ug refs {GuardOutput.FileName(asset)}); Unity would give it a new GUID and they lose it.\n"
                    + "  Remove both files, or neither.");
            }
        }

        return null;
    }

    // c: `.meta` files copied into the project, alone or inside a copied folder: their GUIDs are now twice in the project.
    private GuardNote? CopiedMetas(GuardContext context, ShellOp op)
    {
        var metas = new List<string>();
        var folders = new List<(string Folder, int Metas)>();
        foreach (string source in Assets(op))
        {
            if (IsMeta(source))
            {
                if (context.Graph!.AssetGuid(source) is not null)
                {
                    metas.Add(source);
                }
            }
            else if (Directory.Exists(context.FullPath(source)) && context.FilesUnder(source).Count(IsMeta) is > 0 and int count)
            {
                folders.Add((source, count));
            }
        }

        if (metas.Count == 0 && folders.Count == 0)
        {
            return null;
        }

        string what = folders.Count > 0
            ? $"{GuardOutput.Count(folders.Sum(f => f.Metas) + metas.Count, ".meta file")} under {folders[0].Folder}"
                + (folders.Count > 1 ? $" (+{folders.Count - 1} more {(folders.Count == 2 ? "folder" : "folders")})" : "") + " (a folder copy copies metas)"
            : metas[0] + (metas.Count > 1 ? $" (+{metas.Count - 1} more)" : "");
        return new GuardNote(Case, op.Verb + " " + string.Join(" ", metas.Concat(folders.Select(f => f.Folder))) + " " + op.Destination,
            $"ungrep: this {op.Verb} copies {what}.\n"
            + "  The copies share their GUIDs with the originals, so references resolve to either at random.\n"
            + "  Copy the files without the .meta files and let Unity import them, or duplicate the folder in the Project window.");
    }

    private static IEnumerable<string> Assets(ShellOp op) => op.Sources.OfType<string>().Where(GuardContext.InAssets);

    private static HashSet<string> Sources(IReadOnlyList<ShellOp> ops, FileOpKind kind) =>
        [.. ops.Where(o => o.Kind == kind).SelectMany(o => o.Sources).OfType<string>()];

    private static bool IsMeta(string path) => path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

    // How many assets reference the asset at the path, 0 when its `.meta` is not in the graph.
    private static int Referrers(GuardContext context, string asset) =>
        context.Graph!.AssetGuid(asset + ".meta") is { } guid ? context.Graph.AssetReferrers(guid, asset).Count : 0;
}
