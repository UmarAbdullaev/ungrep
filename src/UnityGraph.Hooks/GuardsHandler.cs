using System.Text.Json;
using UnityGraph.Core;

namespace UnityGraph.Hooks;

/// <summary>
/// The guards (SPEC-guards): before an edit or shell call lands, say what serialized data, `.meta`
/// file or vendored code it would cost. Reads the graph, the config and the call only; silent
/// unless a case fires; warns by default, denies only where the user set `deny`.
/// </summary>
public sealed class GuardsHandler : IHookHandler
{
    private static readonly IGuard[] _guards = [new FieldGuard(), new SerializeReferenceGuard(), new MetaGuard(), new VendoredGuard(), new LintGuard()];

    public string Module => "guards";

    public bool Handles(HookInput input) =>
        input.EventName == "PreToolUse" && input.ToolName is "Bash" or "PowerShell" or "Edit" or "MultiEdit" or "Write" or "NotebookEdit";

    public string? Handle(HookInput input, UnityProject project, CancellationToken cancel)
    {
        if (input.ToolInput.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        using var context = new GuardContext(input, project, UnityGraphConfig.Load(project.Root));
        var session = new GuardSession(project.Root, input.SessionId);
        var shown = new List<(GuardNote, GuardMode)>();
        foreach ((GuardNote note, GuardMode mode) in Check(context, cancel))
        {
            // A deny is never filtered: the retried call must meet it again.
            if (mode == GuardMode.Deny || session.First(note.Case + " " + note.Subject))
            {
                shown.Add((note, mode));
            }
        }

        return GuardOutput.Render(shown);
    }

    /// <summary>Every note the cases that are not off give for the call, with each case's mode; no repeat filter.</summary>
    internal static List<(GuardNote Note, GuardMode Mode)> Check(GuardContext context, CancellationToken cancel)
    {
        var notes = new List<(GuardNote Note, GuardMode Mode)>();
        foreach (IGuard guard in _guards)
        {
            GuardMode mode = context.Config.Guards.ModeOf(guard.Case);
            if (mode == GuardMode.Off)
            {
                continue;
            }

            cancel.ThrowIfCancellationRequested();
            notes.AddRange(guard.Check(context).Select(note => (note, mode)));
        }

        return notes;
    }
}

/// <summary>The notes a session has had, so each prints once (SPEC-guards, shared rule 5).</summary>
internal sealed class GuardSession(string projectRoot, string? sessionId)
{
    private readonly string _path = SessionMarker.PathOf(projectRoot, "guards", sessionId);
    private HashSet<string>? _fired;

    /// <summary>True the first time the key is seen in the session; remembers it.</summary>
    public bool First(string key)
    {
        key = key.ReplaceLineEndings(" ");
        _fired ??= File.Exists(_path) ? [.. File.ReadAllLines(_path)] : [];
        if (!_fired.Add(key))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, key + "\n");
        return true;
    }
}
