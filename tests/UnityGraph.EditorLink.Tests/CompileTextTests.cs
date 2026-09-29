using System.Globalization;
using System.Text;

namespace UnityGraph.EditorLink.Tests;

/// <summary>
/// The `ug compile` text, and Editor.log reading on logs recorded from BridgeHost batch runs
/// (Unity 6000.3.14f1 and 2022.3.62f1; project path replaced by D:/Work/BridgeHost).
/// </summary>
public sealed class CompileTextTests : IDisposable
{
    private const string Recorded = "D:/Work/BridgeHost";

    private static readonly string _logs = Path.Combine(AppContext.BaseDirectory, "Fixtures", "logs");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-log-" + Guid.NewGuid().ToString("N")[..8]);

    public CompileTextTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static CompileMessage Error(string file, int line, int column, string code, string text, string assembly = "Assembly-CSharp") =>
        new(assembly, file, line, column, "error", code, text);

    private static CompileMessage Warning(string file, int line, int column, string code, string text) =>
        new("Assembly-CSharp", file, line, column, "warning", code, text);

    private static readonly CompileMessage[] _mixed =
    [
        Error("Assets/Scripts/Lab/Turntable.cs", 17, 9, "CS0103", "The name 'spd' does not exist in the current context"),
        Warning("Assets/Scripts/Lab/LabStage.cs", 12, 17, "CS0168", "The variable 'e' is declared but never used"),
        Error("Assets/Scripts/Lab/LabStage.cs", 30, 13, "CS1061", "'Turntable' does not contain a definition for 'Spin'"),
        // The same error from the editor assembly that also compiles the file.
        Error("Assets/Scripts/Lab/Turntable.cs", 17, 9, "CS0103", "The name 'spd' does not exist in the current context", "Assembly-CSharp-Editor"),
        Error("Assets/Scripts/Lab/LabStage.cs", 30, 2, "CS1002", "; expected"),
        new(null, null, 0, 0, "error", null, "Assembly has reference to non-existent assembly 'Missing'"),
    ];

    private static CompileOutcome Outcome(bool changed, params CompileMessage[] messages) =>
        new(changed, new CompileResult(4, null, null, messages), null, false, TimeSpan.FromSeconds(4.13));

    [Fact]
    public void Errors_sorted_once_each() => Golden.Match(CompileText.Format(Outcome(changed: true, _mixed), warnings: false));

    [Fact]
    public void With_warnings() => Golden.Match(CompileText.Format(Outcome(changed: true, _mixed), warnings: true));

    [Fact]
    public void No_script_changes() => Golden.Match(CompileText.Format(Outcome(changed: false), warnings: false));

    [Fact]
    public void At_most_twenty_lines()
    {
        CompileMessage[] many = Enumerable.Range(1, 23)
            .Select(i => Error("Assets/Scripts/Many.cs", i, 1, "CS0246", "The type or namespace name 'T" + i.ToString(CultureInfo.InvariantCulture) + "' could not be found"))
            .ToArray();

        Golden.Match(CompileText.Format(Outcome(changed: true, many), warnings: false));
    }

    [Theory]
    [InlineData("unity6-failed.log")]
    [InlineData("unity6-fixed.log")]
    [InlineData("unity6-no-compile.log")]
    [InlineData("unity2022-failed.log")]
    public void Recorded_logs(string log)
    {
        EditorLogAnswer answer = EditorLog.Read(File.ReadAllText(Path.Combine(_logs, log)), Recorded);

        var text = new StringBuilder();
        text.Append("project: ").Append(answer.Project).Append(" (this one: ").Append(answer.IsThisProject ? "yes" : "no").Append(")\n");
        text.Append(answer.LastCompile == null
            ? "no finished compile\n"
            : (answer.LastCompile.Failed ? "failed: " : "succeeded: ") + CompileText.Counts(answer.LastCompile.Messages) + "\n" + CompileText.Lines(answer.LastCompile.Messages, warnings: true));
        Golden.Match(text.ToString(), "txt", test: "Recorded_logs." + Path.GetFileNameWithoutExtension(log));
    }

    [Fact]
    public void Hub_passes_the_project_in_lowercase_with_backslashes()
    {
        string log = "COMMAND LINE ARGUMENTS:\nC:\\Unity.exe\n-projectpath\nD:\\Work\\BridgeHost\\\n-useHub\n";

        EditorLogAnswer answer = EditorLog.Read(log, Recorded);

        Assert.True(answer.IsThisProject);
        Assert.Null(answer.LastCompile);
    }

    [Fact]
    public void Another_projects_log_gives_no_errors()
    {
        string log = File.ReadAllText(Path.Combine(_logs, "unity6-failed.log"));

        EditorLogAnswer answer = EditorLog.Read(log, "D:/Work/Other");

        Assert.Equal(Recorded, answer.Project);
        Assert.False(answer.IsThisProject);
        Assert.Null(answer.LastCompile);
    }

    [Fact]
    public void A_log_without_a_project_line()
    {
        EditorLogAnswer answer = EditorLog.Read("*** Tundra build failed (0.66 seconds)\n", Recorded);

        Assert.Null(answer.Project);
        Assert.False(answer.IsThisProject);
    }

    [Fact]
    public void Message_lines()
    {
        Assert.Equal(
            new CompileMessage(null, "Assets/A b/C.cs", 3, 14, "warning", "CS0168", "The variable 'e' is declared but never used"),
            EditorLog.Message("Assets\\A b\\C.cs(3,14): warning CS0168: The variable 'e' is declared but never used\r"));
        Assert.Null(EditorLog.Message("Assets/A.cs: error: no position"));
        Assert.Null(EditorLog.Message("*** Tundra build failed"));
    }

    [Fact]
    public void The_default_log_is_in_the_users_folder()
    {
        string? path = EditorLog.DefaultPath();

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.EndsWith("Editor.log", path);
        }
        else
        {
            Assert.Null(path);
        }
    }

    [Fact]
    public void Fallback_reads_the_projects_log()
    {
        string log = WriteLog("unity6-failed.log", new DateTime(2026, 9, 29, 19, 49, 38, DateTimeKind.Local));

        Golden.Match(CompileText.Fallback(_root, "no bridge.json", log, warnings: false));
    }

    [Fact]
    public void Fallback_prefers_a_newer_compile_json()
    {
        string log = WriteLog("unity6-failed.log", new DateTime(2026, 9, 29, 19, 49, 38, DateTimeKind.Local));
        string json = Path.Combine(_root, "Library", "unitygraph", "compile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        File.WriteAllText(json, "{\"compile\":2,\"started\":\"2026-09-29T17:49:47Z\",\"finished\":\"2026-09-29T17:49:48Z\",\"messages\":[]}");
        File.SetLastWriteTime(json, new DateTime(2026, 9, 29, 19, 49, 48, DateTimeKind.Local));

        Golden.Match(CompileText.Fallback(_root, "stale bridge.json: Unity (pid 48716) is not running", log, warnings: false));
    }

    [Fact]
    public void Fallback_skips_an_unreadable_compile_json()
    {
        string log = WriteLog("unity6-no-compile.log", new DateTime(2026, 9, 29, 19, 49, 14, DateTimeKind.Local));
        string json = Path.Combine(_root, "Library", "unitygraph", "compile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        File.WriteAllText(json, "{");
        File.SetLastWriteTime(json, new DateTime(2026, 9, 29, 19, 50, 0, DateTimeKind.Local));

        Assert.Equal(
            "Unity not reachable (no bridge.json); Editor.log of 2026-09-29 19:49:14 shows no finished compile\n",
            CompileText.Fallback(_root, "no bridge.json", log, warnings: false));
    }

    [Fact]
    public void Fallback_on_another_projects_log_or_none()
    {
        string log = Path.Combine(_root, "Editor.log");
        File.WriteAllText(log, File.ReadAllText(Path.Combine(_logs, "unity6-failed.log")));

        Assert.Equal(
            "Unity not reachable (no bridge.json); Editor.log is another project's (D:/Work/BridgeHost)\n",
            CompileText.Fallback(_root, "no bridge.json", log, warnings: false));
        File.WriteAllText(log, "nothing\n");
        Assert.Equal(
            "Unity not reachable (no bridge.json); Editor.log is of no known project\n",
            CompileText.Fallback(_root, "no bridge.json", log, warnings: false));
        Assert.Equal(
            "Unity not reachable (no bridge.json); no Editor.log or compile.json to read\n",
            CompileText.Fallback(_root, "no bridge.json", Path.Combine(_root, "missing.log"), warnings: false));
        Assert.Equal(
            "Unity not reachable (no bridge.json); no Editor.log or compile.json to read\n",
            CompileText.Fallback(_root, "no bridge.json", null, warnings: false));
    }

    // A recorded log moved to this test's project folder.
    private string WriteLog(string name, DateTime written)
    {
        string path = Path.Combine(_root, "Editor.log");
        File.WriteAllText(path, File.ReadAllText(Path.Combine(_logs, name)).Replace(Recorded, _root.Replace('\\', '/'), StringComparison.Ordinal));
        File.SetLastWriteTime(path, written);
        return path;
    }
}
