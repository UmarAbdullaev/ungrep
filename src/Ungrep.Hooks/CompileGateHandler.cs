using System.Text.Json;
using Ungrep.Core;
using Ungrep.EditorLink;

namespace Ungrep.Hooks;

/// <summary>
/// The compile gate of unity-loop (SPEC-unity-loop, Compile gate). After an edit tool changed a
/// script the session is marked in `Library/ungrep/`; the Stop hook then asks Unity to
/// compile and, on errors, blocks the stop with the first 20 of them. Silent when clean, when
/// Unity is out of reach or the editor is playing. Only ever writes its marker file.
/// </summary>
public sealed class CompileGateHandler : IHookHandler
{
    /// <summary>How long the Stop hook waits for Unity's compile; the dispatcher's Stop limit is longer.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static readonly string[] _scriptExtensions = [".cs", ".asmdef", ".asmref"];

    // check: asks Unity whether the scripts of the project compile, within the deadline.
    private readonly Func<string, TimeSpan, GateAnswer> _check;

    public CompileGateHandler(Func<string, TimeSpan, GateAnswer> check) => _check = check;

    public string Module => "unity-loop";

    public bool Handles(HookInput input) =>
        input.EventName == "Stop" || (input.EventName == "PostToolUse" && input.ToolName is "Edit" or "MultiEdit" or "Write" or "NotebookEdit");

    public string? Handle(HookInput input, UnityProject project, CancellationToken cancel)
    {
        string marker = SessionMarker.PathOf(project.Root, "gate", input.SessionId);
        if (input.EventName == "PostToolUse")
        {
            Track(input, project.Root, marker);
            return null;
        }

        if (!File.Exists(marker) || !UngrepConfig.Load(project.Root).IsEnabled("editor-link"))
        {
            return null;
        }

        GateAnswer answer = _check(project.Root, Deadline);
        if (answer.Result == GateResult.Playing)
        {
            return null; // checked at the next Stop after play mode ends
        }

        File.Delete(marker);
        return answer.Result == GateResult.Errors ? JsonSerializer.Serialize(new { decision = "block", reason = answer.Text }) : null;
    }

    // A script under Assets/ or Packages/ of the project: remember the edit, say nothing.
    private static void Track(HookInput input, string projectRoot, string marker)
    {
        if (!input.ToolInput.TryGetProperty("file_path", out JsonElement value) || value.GetString() is not { Length: > 0 } file
            || !_scriptExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        string relative = Path.GetRelativePath(projectRoot, Path.GetFullPath(file, projectRoot)).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || !(relative.StartsWith("Assets/", StringComparison.Ordinal) || relative.StartsWith("Packages/", StringComparison.Ordinal)))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.AppendAllText(marker, relative + "\n");
    }
}
