namespace UnityGraph.Hooks;

/// <summary>
/// Case 4 (`vendored`): an edit inside a third-party folder, lost with the next package update.
/// Reads the config only, so it works before the first index.
/// </summary>
internal sealed class VendoredGuard : IGuard
{
    public string Case => "vendored";

    public IEnumerable<GuardNote> Check(GuardContext context)
    {
        if (EditCall.From(context.Input) is not { } call || context.ProjectPath(call.FilePath) is not { Length: > 0 } path
            || path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            || context.Config.Vendored.Prefixes.FirstOrDefault(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is not { } folder)
        {
            yield break;
        }

        // One note per folder and session: the next file in the same folder needs no new note.
        yield return new GuardNote(Case, folder,
            $"unitygraph: {path} is in a vendored folder ({folder}); local edits are lost when the package is updated.\n"
            + "  Prefer a wrapper, subclass or partial class outside it. If this edit is intended, set guards.vendored to \"off\"\n"
            + "  in .claude/unitygraph.json, or take the folder out of `vendored`.");
    }
}
