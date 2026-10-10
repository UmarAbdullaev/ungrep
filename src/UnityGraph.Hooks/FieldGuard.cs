using UnityGraph.Query;

namespace UnityGraph.Hooks;

/// <summary>
/// Case 1 (`field-removed`): an edit that renames or removes a serialized field or property whose
/// owner type has assets holding data. A new text that still names the member (including as
/// `[FormerlySerializedAs("name")]`) is the safe form and stays silent.
/// </summary>
internal sealed class FieldGuard : IGuard
{
    public string Case => "field-removed";

    public IEnumerable<GuardNote> Check(GuardContext context)
    {
        if (EditCall.From(context.Input) is not { } call || call.Edits.Count == 0 && call.Content is null
            || context.OwnScript(call.FilePath) is not { } path || context.Graph is not { } graph || !context.IsFresh(path))
        {
            yield break;
        }

        IReadOnlyList<SerializedMember> members = graph.SerializedMembers(path);
        if (members.Count == 0)
        {
            yield break;
        }

        List<SerializedMember> gone;
        if (call.Content is { } content)
        {
            gone = [.. members.Where(m => !EditCall.Identifier(m.Name).IsMatch(content))];
        }
        else if (call.Locate(EditCall.Lf(File.ReadAllText(context.FullPath(path)))) is { } located)
        {
            gone = [.. members.Where(m => located.Any(l => Removes(l, m)))];
        }
        else
        {
            yield break; // the call would fail anyway
        }

        var holders = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        gone = [.. gone.Where(m => (holders.TryGetValue(m.Owner, out IReadOnlyList<string>? h) ? h : holders[m.Owner] = graph.DataHolders(m.Owner)).Count > 0)];
        if (gone.Count == 0)
        {
            yield break;
        }

        SerializedMember first = gone[0];
        string[] owners = [.. gone.Select(m => m.Owner).Distinct(StringComparer.Ordinal)];
        string[] files = [.. owners.SelectMany(o => holders[o]).Distinct(StringComparer.Ordinal)];
        string ownerName = first.OwnerQualified;
        string others = owners.Length > 1 ? $" and {GuardOutput.Count(owners.Length - 1, "more type")}" : "";
        string what = gone.Count == 1
            ? $"{Display(first)} is serialized and this edit renames or removes it."
            : $"{Display(first)} (+{gone.Count - 1} more: {string.Join(", ", gone.Skip(1).Select(m => m.Name))}) are serialized and this edit renames or removes them.";
        string attribute = first.IsProperty
            ? $"[field: FormerlySerializedAs(\"<{first.Name}>k__BackingField\")]"
            : $"[FormerlySerializedAs(\"{first.Name}\")]";
        string onNew = gone.Count == 1 ? "on the new name" : "on each new name, with its old name";
        yield return new GuardNote(Case, path + " " + string.Join(" ", gone.Select(m => m.Key)),
            $"unitygraph: {what}\n"
            + $"  Saved values would be lost: up to {GuardOutput.Assets(files)} {(files.Length == 1 ? "holds" : "hold")} data of {ownerName}{others} (ug impact {first.OwnerName}.{first.Name}).\n"
            + $"  To keep them, put {attribute} {onNew}; to drop them on purpose, ignore this note.");
    }

    // The replacement covers the member's lines and names it, and its new text no longer does.
    private static bool Removes(LocatedReplacement located, SerializedMember member) =>
        located.Lines.Any(r => r.Start <= member.LineEnd && r.End >= member.LineStart)
        && EditCall.Identifier(member.Name).IsMatch(located.Edit.Old)
        && !EditCall.Identifier(member.Name).IsMatch(located.Edit.New);

    private static string Display(SerializedMember member) => $"{member.OwnerName}.{member.Name}" + (member.IsProperty ? " property" : "");
}
