using System.Text;
using System.Text.Json;
using Ungrep.Core;

namespace Ungrep.Hooks.Tests;

/// <summary>
/// The guards (SPEC-guards) on PreToolUse payloads against the indexed fixture: each case's note,
/// its safe and nothing-at-stake twins, the silent cases, the modes and the repeat filter.
/// </summary>
public sealed class GuardsHandlerTests(GuardFixture fixture) : IClassFixture<GuardFixture>, IDisposable
{
    private const string Speed = "[SerializeField] private float _speed;";
    private const string Tint = "[field: SerializeField] public Color Tint { get; private set; }";
    private const string Turntable = "Assets/Scripts/Lab/Turntable.cs";
    private const string Behaviours = "Assets/Scripts/Core/Behaviours.cs";

    private static int _sessions;

    private string Root => fixture.Root;

    public void Dispose()
    {
        if (File.Exists(Path.Combine(Root, UngrepConfig.RelativePath)))
        {
            File.Delete(Path.Combine(Root, UngrepConfig.RelativePath));
        }
    }

    [Fact]
    public void Notes_for_each_case()
    {
        var text = new StringBuilder();
        foreach ((string name, string tool, object input) in Cases())
        {
            text.Append("## ").Append(name).Append('\n').Append(Note(Handle(Payload(tool, input)))).Append("\n\n");
        }

        Golden.Match(text.ToString());
    }

    [Fact]
    public void Lint_notes()
    {
        Config("""
            { "guards": { "lint": { "patterns": [
              { "pattern": "GameObject\\.Find\\(", "message": "look it up once and cache it, or serialize a reference" },
              { "pattern": "SendMessage\\(" },
            ] } } }
            """);
        string labStage = fixture.Abs("Assets/Scripts/Lab/LabStage.cs");
        var cases = new (string Name, string Tool, object Input)[]
        {
            ("added call", "Edit", Edit(labStage, "Count++;", "Count++;\n            var t = GameObject.Find(\"Turntable\");")),
            ("call already there (unchanged line)", "Edit", Edit(labStage, "            SendMessage(\"OnOpened\");", "            SendMessage(\"OnOpened\");\n            Count++;")),
            ("commented out", "Edit", Edit(labStage, "Count++;", "Count++;\n            // GameObject.Find(\"Turntable\") is slow")),
            ("four hits, a long one clipped", "Edit", Edit(labStage, "Count++;",
                "Count++;\n            GameObject.Find(\"" + new string('x', 90) + "\");\n            SendMessage(\"C\");\n            GameObject.Find(\"A\");\n            GameObject.Find(\"B\");")),
            ("Write adds a line", "Write", new { file_path = fixture.Abs("Assets/Scripts/Lab/New.cs"), content = "class New { void A() { UnityEngine.GameObject.Find(\"A\"); } }\n" }),
            ("vendored file (case 4 speaks)", "Edit", Edit(fixture.Abs("Assets/3rd Party/Tweens/Tween.cs"), "Stop();", "Stop();\n            GameObject.Find(\"A\");")),
        };

        var text = new StringBuilder();
        foreach ((string name, string tool, object input) in cases)
        {
            text.Append("## ").Append(name).Append('\n').Append(Note(Handle(Payload(tool, input)))).Append("\n\n");
        }

        Golden.Match(text.ToString());
    }

    [Fact]
    public void Warn_and_deny_outputs()
    {
        string payload = Payload("Edit", Edit(fixture.Abs(Turntable), Speed, "[SerializeField] private float _rate;"));
        string warn = Handle(payload)!;
        Config("""{ "guards": { "field-removed": "deny" } }""");
        string deny = Handle(payload)!;

        Golden.Match(warn + "\n" + deny + "\n");
    }

    [Fact]
    public void A_note_prints_once_per_session_and_subject()
    {
        string first = Payload("Edit", Edit(fixture.Abs(Turntable), Speed, "[SerializeField] private float _rate;"), "repeat-a");
        string again = Payload("Edit", Edit(fixture.Abs(Turntable), Speed, "[SerializeField] private float _turn;"), "repeat-a");
        string other = Payload("Edit", Edit(fixture.Abs(Turntable), Tint, "[field: SerializeField] public Color Color { get; private set; }"), "repeat-a");

        Assert.NotNull(Handle(first));
        Assert.Null(Handle(first));
        Assert.Null(Handle(again));
        Assert.NotNull(Handle(other));
        Assert.NotNull(Handle(Payload("Edit", Edit(fixture.Abs(Turntable), Speed, "[SerializeField] private float _rate;"), "repeat-b")));
        Assert.True(File.Exists(Path.Combine(Root, "Library", "ungrep", "guards-repeat-a")));
    }

    [Fact]
    public void A_deny_is_never_filtered()
    {
        Config("""{ "guards": { "meta": "deny" } }""");
        string payload = Payload("Bash", Shell("rm Assets/Prefabs/Turntable.prefab.meta"), "deny-a");

        string? first = Handle(payload);

        Assert.Contains("\"permissionDecision\":\"deny\"", first, StringComparison.Ordinal);
        Assert.Equal(first, Handle(payload));
    }

    [Fact]
    public void A_case_turned_off_is_silent()
    {
        Config("""{ "guards": { "field-removed": "off", "meta": "off", "vendored": "off", "serialize-reference": "off", "lint": { "mode": "off", "patterns": [ { "pattern": "Find" } ] } } }""");

        Assert.Null(Handle(Payload("Edit", Edit(fixture.Abs(Turntable), Speed, "[SerializeField] private float _rate;"))));
        Assert.Null(Handle(Payload("Bash", Shell("rm Assets/Prefabs/Turntable.prefab.meta"))));
        Assert.Null(Handle(Payload("Write", new { file_path = fixture.Abs("Assets/Plugins/A.cs"), content = "x" })));
        Assert.Null(Handle(Payload("Edit", Edit(fixture.Abs(Behaviours), "public class SpinBehaviour", "public class TurnBehaviour"))));
        Assert.Null(Handle(Payload("Edit", Edit(fixture.Abs(Turntable), "Stop();", "Stop(); GameObject.Find(\"x\");"))));
    }

    [Fact]
    public void Through_the_dispatcher_the_module_switch_and_a_bad_config_are_silent()
    {
        string payload = Payload("Bash", Shell("rm Assets/Prefabs/Turntable.prefab.meta"));
        string log = Path.Combine(Root, HookDispatcher.ErrorLogPath);
        File.Delete(log);

        Assert.Contains("this rm deletes Assets/Prefabs/Turntable.prefab.meta", Dispatch(payload), StringComparison.Ordinal);

        Config("""{ "modules": { "guards": false } }""");
        Assert.Equal("", Dispatch(Payload("Bash", Shell("rm Assets/Prefabs/Turntable.prefab.meta"))));

        Config("""{ "guards": { "meta": "maybe" } }""");
        Assert.Equal("", Dispatch(Payload("Bash", Shell("rm Assets/Prefabs/Turntable.prefab.meta"))));
        Assert.Contains("PreToolUse guards.meta must be \"warn\", \"deny\" or \"off\"", File.ReadAllText(log), StringComparison.Ordinal);
        File.Delete(log);
    }

    [Fact]
    public void Silent_when_it_cannot_know()
    {
        string rename = "[SerializeField] private float _rate;";
        Assert.Null(Handle(Payload("Edit", Edit(fixture.Abs(Turntable), "[SerializeField] private float _nothing;", rename))));
        Assert.Null(Handle(Payload("Edit", Edit(fixture.Abs(Turntable), "_speed", "_rate"))));
        Assert.Null(Handle(Payload("Edit", Edit(fixture.Abs(Turntable), "", rename))));
        Assert.Null(Handle(Payload("Edit", new { file_path = fixture.Abs(Turntable), old_string = Speed })));
        Assert.Null(Handle(Payload("Edit", null)));
        Assert.Null(Handle(Payload("Edit", "not an object")));
        Assert.Null(Handle(Payload("Bash", new { cmd = "rm Assets/Prefabs/Turntable.prefab.meta" })));

        string outside = Path.Combine(Path.GetTempPath(), "ug-outside", Turntable).Replace('\\', '/');
        Assert.Null(Handle(Payload("Edit", Edit(outside, Speed, rename))));
        Assert.Null(Handle(Payload("Bash", Shell("rm Prefabs/Turntable.prefab.meta"), cwd: fixture.Abs("Assets/Scripts"))));
        Assert.Null(Handle(Payload("Bash", Shell("cd $DIR && rm Assets/Prefabs/Turntable.prefab.meta"))));
    }

    [Fact]
    public void Relative_paths_follow_the_cwd_and_cd()
    {
        string scripts = fixture.Abs("Assets/Scripts");

        Assert.NotNull(Handle(Payload("Bash", Shell("rm ../Prefabs/Turntable.prefab.meta"), cwd: scripts)));
        Assert.NotNull(Handle(Payload("Bash", Shell("cd .. && rm Prefabs/Turntable.prefab.meta"), cwd: scripts)));
        Assert.NotNull(Handle(Payload("PowerShell", Shell("Set-Location ../Prefabs; Remove-Item Turntable.prefab.meta"), cwd: scripts)));
        Assert.NotNull(Handle(Payload("Bash", Shell($"rm {fixture.Abs("Assets/Prefabs/Turntable.prefab.meta")}"), cwd: Path.GetTempPath().Replace('\\', '/'))));
    }

    [Fact]
    public void Silent_for_a_script_changed_since_the_index()
    {
        string path = Path.Combine(Root, Turntable);
        byte[] original = File.ReadAllBytes(path);
        string payload = Payload("Edit", Edit(fixture.Abs(Turntable), Speed, "[SerializeField] private float _rate;"));
        try
        {
            File.AppendAllText(path, "\n");
            Assert.Null(Handle(payload));
        }
        finally
        {
            File.WriteAllBytes(path, original);
        }

        // Same content again: the mtime differs, the hash says it is the indexed file.
        Assert.NotNull(Handle(Payload("Edit", Edit(fixture.Abs(Turntable), Speed, "[SerializeField] private float _rate;"))));
    }

    [Fact]
    public void Without_a_graph_only_the_vendored_case_speaks()
    {
        string root = Path.Combine(Path.GetTempPath(), "ug-guards-nograph-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Scripts"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.14f1\n");
            File.Copy(Path.Combine(Root, Turntable), Path.Combine(root, "Assets", "Scripts", "Turntable.cs"));
            string abs = root.Replace('\\', '/');

            Assert.Null(Handle(Payload("Edit", Edit(abs + "/Assets/Scripts/Turntable.cs", Speed, "[SerializeField] private float _rate;"), cwd: abs), root));
            Assert.Null(Handle(Payload("Bash", Shell("rm Assets/Scripts/Turntable.cs.meta"), cwd: abs), root));
            Assert.Contains("is in a vendored folder (Assets/Plugins/)",
                Note(Handle(Payload("Write", new { file_path = abs + "/Assets/Plugins/A.cs", content = "x" }, cwd: abs), root)), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(root, "Library", "ungrep", "graph.db")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Assets/Plugins/Foo/Bar.cs", "Assets/Plugins/")]
    [InlineData("Assets/3rd Party/X.cs", "Assets/3rd Party/")]
    [InlineData("Assets/3rdParty/X.cs", "Assets/3rdParty/")]
    [InlineData("Assets/Third Party/X.cs", "Assets/Third Party/")]
    [InlineData("Assets/ThirdParty/X.cs", "Assets/ThirdParty/")]
    [InlineData("Assets/Vendor/X.cs", "Assets/Vendor/")]
    [InlineData("Assets/AssetStore/X.shader", "Assets/AssetStore/")]
    [InlineData("assets/plugins/x.cs", "Assets/Plugins/")]
    [InlineData("Packages/com.example.tools/Runtime/Tool.cs", "Packages/")]
    [InlineData("Assets/Plugins/Foo/Bar.cs.meta", null)]
    [InlineData("Assets/Scripts/Plugins/X.cs", null)]
    [InlineData("Library/PackageCache/com.x/Y.cs", null)]
    public void Each_default_vendored_folder(string file, string? folder)
    {
        string? note = Handle(Payload("Write", new { file_path = fixture.Abs(file), content = "x" }));

        if (folder is null)
        {
            Assert.Null(note);
        }
        else
        {
            Assert.StartsWith($"ungrep: {file} is in a vendored folder ({folder});", Note(note), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_custom_vendored_folder_replaces_the_defaults_and_speaks_once_per_folder()
    {
        Config("""{ "vendored": ["Assets/External/"] }""");

        string session = "vendored-" + Interlocked.Increment(ref _sessions);
        Assert.NotNull(Handle(Payload("Write", new { file_path = fixture.Abs("Assets/External/A.cs"), content = "x" }, session)));
        Assert.Null(Handle(Payload("Write", new { file_path = fixture.Abs("Assets/External/B.cs"), content = "x" }, session)));
        Assert.Null(Handle(Payload("Write", new { file_path = fixture.Abs("Assets/Plugins/A.cs"), content = "x" }, session)));
    }

    [Fact]
    public void Git_bash_drive_paths_resolve_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string full = Path.GetFullPath(Root).Replace('\\', '/');
        string msys = "/" + char.ToLowerInvariant(full[0]) + full[2..];

        Assert.Contains("this rm deletes Assets/Prefabs/Turntable.prefab.meta",
            Note(Handle(Payload("Bash", Shell($"rm {msys}/Assets/Prefabs/Turntable.prefab.meta"), cwd: msys))), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Assets/Scripts/Core", "Game.Core")]
    [InlineData("Assets/Scripts/Core/Not/Yet/There", "Game.Core")]
    [InlineData("Assets/Scripts/Extra", "Game.Core")]
    [InlineData("Assets/Scripts/Named", "Game.Core")]
    [InlineData("Assets/Scripts/Lab", "Assembly-CSharp")]
    [InlineData("Assets", "Assembly-CSharp")]
    [InlineData("Assets/Editor", "Assembly-CSharp-Editor")]
    [InlineData("Assets/Scripts/Lab/Editor/Tools", "Assembly-CSharp-Editor")]
    [InlineData("Assets/Plugins/Foo", "Assembly-CSharp-firstpass")]
    [InlineData("Assets/Standard Assets/Editor", "Assembly-CSharp-Editor-firstpass")]
    [InlineData("Packages/com.example.tools/Runtime", null)]
    public void The_assembly_of_a_folder(string folder, string? assembly)
    {
        using var context = new GuardContext(HookInput.Parse("PreToolUse", "{}"), UnityProject.Find(Root)!, UngrepConfig.Default);

        Assert.Equal(assembly, ScriptAssembly.Of(context, folder));
    }

    private IEnumerable<(string Name, string Tool, object Input)> Cases()
    {
        string turntable = fixture.Abs(Turntable);
        string behaviours = fixture.Abs(Behaviours);
        string turntableText = File.ReadAllText(Path.Combine(Root, Turntable));

        // Case 1: field-removed.
        yield return ("field: rename", "Edit", Edit(turntable, Speed, "[SerializeField] private float _rate;"));
        yield return ("field: rename with FormerlySerializedAs (safe)", "Edit",
            Edit(turntable, Speed, "[FormerlySerializedAs(\"_speed\")]\n        [SerializeField] private float _rate;"));
        yield return ("field: rename with replace_all", "Edit", Edit(turntable, "_speed", "_rate", all: true));
        yield return ("field: removal", "Edit",
            Edit(turntable, "        [FormerlySerializedAs(\"rpm\")]\n        [FormerlySerializedAs(\"_rpm\")]\n        " + Speed + "\n", ""));
        yield return ("field: property rename", "Edit", Edit(turntable, Tint, "[field: SerializeField] public Color Color { get; private set; }"));
        yield return ("field: two members in a MultiEdit", "MultiEdit", new
        {
            file_path = turntable,
            edits = new[]
            {
                new { old_string = Speed, new_string = "[SerializeField] private float _rate;" },
                new { old_string = Tint, new_string = "[field: SerializeField] public Color Color { get; private set; }" },
            },
        });
        yield return ("field: Write that drops a field", "Write", new { file_path = turntable, content = turntableText.Replace("_speed", "_rate", StringComparison.Ordinal) });
        yield return ("field: Write that keeps every field (safe)", "Write", new { file_path = turntable, content = turntableText + "\n// done\n" });
        yield return ("field: edit beside the fields (safe)", "Edit", Edit(turntable, "public void Stop() => _speed = 0;", "public void Stop() => _speed = 0f;"));
        yield return ("field: LabStage.speed, held by a prefab", "Edit", Edit(fixture.Abs("Assets/Scripts/Lab/LabStage.cs"), "public float speed = 2f;", "public float pace = 2f;"));
        yield return ("field: no asset holds Unheld (nothing at stake)", "Edit", Edit(fixture.Abs("Assets/Scripts/Lab/Unheld.cs"), "private int _count;", "private int _total;"));

        // Case 2: serialize-reference.
        yield return ("serialize-reference: type rename", "Edit", Edit(behaviours, "public class SpinBehaviour : IStageBehaviour", "public class TurnBehaviour : IStageBehaviour"));
        yield return ("serialize-reference: rename with MovedFrom (safe)", "Edit", Edit(behaviours,
            "    [Serializable]\n    public class SpinBehaviour : IStageBehaviour",
            "    [Serializable, MovedFrom(true, sourceClassName: \"SpinBehaviour\")]\n    public class TurnBehaviour : IStageBehaviour"));
        yield return ("serialize-reference: namespace change", "Edit", Edit(behaviours, "namespace Game.Core", "namespace Game.Behaviours"));
        yield return ("serialize-reference: Write that renames the type", "Write",
            new { file_path = behaviours, content = File.ReadAllText(Path.Combine(Root, Behaviours)).Replace("class SpinBehaviour", "class TurnBehaviour", StringComparison.Ordinal) });
        yield return ("serialize-reference: interface rename (no instance is stored by its name)", "Edit", Edit(behaviours, "public interface IStageBehaviour", "public interface IBehaviour"));
        yield return ("serialize-reference: a type no such field stores (nothing at stake)", "Edit",
            Edit(fixture.Abs("Assets/Scripts/Core/Stage.cs"), "public class StageData", "public class StageInfo"));
        yield return ("serialize-reference: mv into another assembly", "Bash",
            Shell("mv Assets/Scripts/Core/Behaviours.cs Assets/Scripts/Core/Behaviours.cs.meta Assets/Scripts/Lab/"));
        yield return ("serialize-reference: Move-Item into another assembly", "PowerShell",
            Shell("Move-Item -Path Assets/Scripts/Core/Behaviours.cs -Destination Assets/Scripts/Lab/Spin.cs; Move-Item Assets/Scripts/Core/Behaviours.cs.meta Assets/Scripts/Lab/Spin.cs.meta"));
        yield return ("serialize-reference: mv inside its assembly (safe)", "Bash",
            Shell("mv Assets/Scripts/Core/Behaviours.cs Assets/Scripts/Core/Spin.cs && mv Assets/Scripts/Core/Behaviours.cs.meta Assets/Scripts/Core/Spin.cs.meta"));

        // Case 3: meta.
        yield return ("meta: mv without the .meta", "Bash", Shell("mv Assets/Prefabs/Turntable.prefab Assets/Prefabs/Old.prefab"));
        yield return ("meta: mv of two assets into a folder, quoted, without their .meta files", "Bash",
            Shell("git mv \"Assets/Prefabs/Turntable.prefab\" 'Assets/Textures/Grid.png' Assets/Data/"));
        yield return ("meta: mv after cd", "Bash", Shell("cd Assets/Prefabs && mv Turntable.prefab Old.prefab"));
        yield return ("meta: Move-Item", "PowerShell", Shell("Move-Item -Path Assets/Prefabs/Turntable.prefab -Destination Assets/Data/Turntable.prefab"));
        yield return ("meta: Rename-Item", "PowerShell", Shell("Rename-Item Assets/Prefabs/Turntable.prefab Platter.prefab"));
        yield return ("meta: mv with the .meta (safe)", "Bash", Shell("mv Assets/Prefabs/Turntable.prefab Assets/Prefabs/Turntable.prefab.meta Assets/Data/"));
        yield return ("meta: mv, then mv of the .meta in a chain (safe)", "Bash",
            Shell("mv Assets/Prefabs/Turntable.prefab Assets/Data/Turntable.prefab && mv Assets/Prefabs/Turntable.prefab.meta Assets/Data/Turntable.prefab.meta"));
        yield return ("meta: mv of an asset nothing references (nothing at stake)", "Bash", Shell("mv Assets/Prefabs/Spinner.prefab Assets/Spinner.prefab"));
        yield return ("meta: mv out of the project (a backup)", "Bash", Shell("mv Assets/Prefabs/Turntable.prefab ../Turntable.prefab"));
        yield return ("meta: mv of a glob (can't know)", "Bash", Shell("mv Assets/Prefabs/*.prefab Assets/Data/"));
        yield return ("meta: mv of a variable (can't know)", "Bash", Shell("mv $SRC Assets/Data/"));
        yield return ("meta: rm of the .meta alone", "Bash", Shell("rm Assets/Prefabs/Turntable.prefab.meta"));
        yield return ("meta: Remove-Item of the .meta alone", "PowerShell", Shell("Remove-Item -LiteralPath 'Assets/Prefabs/Turntable.prefab.meta' -Force"));
        yield return ("meta: rm of both (safe)", "Bash", Shell("rm Assets/Prefabs/Turntable.prefab Assets/Prefabs/Turntable.prefab.meta"));
        yield return ("meta: rm of the asset alone (Unity deletes the orphan .meta)", "Bash", Shell("rm Assets/Prefabs/Turntable.prefab"));
        yield return ("meta: rm of an unreferenced asset's .meta (nothing at stake)", "Bash", Shell("rm Assets/Prefabs/Spinner.prefab.meta"));
        yield return ("meta: here-document that mentions rm", "Bash", Shell("cat <<'EOF' > notes.md\nrm Assets/Prefabs/Turntable.prefab.meta\nEOF"));
        yield return ("meta: cp -r of a folder", "Bash", Shell("cp -r Assets/Prefabs Assets/Prefabs2"));
        yield return ("meta: Copy-Item -Recurse of a folder", "PowerShell", Shell("Copy-Item -Recurse Assets/Prefabs Assets/Prefabs2"));
        yield return ("meta: cp of an asset with its .meta", "Bash", Shell("cp Assets/Prefabs/Turntable.prefab Assets/Prefabs/Turntable.prefab.meta Assets/Data/"));
        yield return ("meta: cp of an asset without its .meta (safe)", "Bash", Shell("cp Assets/Prefabs/Turntable.prefab Assets/Prefabs/Copy.prefab"));
        yield return ("meta: cp -r out of the project (a backup)", "Bash", Shell("cp -r Assets/Prefabs ../backup"));

        // Case 4: vendored.
        yield return ("vendored: Edit in Assets/3rd Party", "Edit", Edit(fixture.Abs("Assets/3rd Party/Tweens/Tween.cs"), "Stop();", "Stop(); Play();"));
        yield return ("vendored: NotebookEdit in Assets/Plugins", "NotebookEdit", new { notebook_path = fixture.Abs("Assets/Plugins/Notes/Readme.ipynb"), new_source = "x" });
        yield return ("vendored: outside the project", "Write", new { file_path = Path.Combine(Path.GetTempPath(), "Assets/Plugins/A.cs").Replace('\\', '/'), content = "x" });
    }

    private void Config(string json)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".claude"));
        File.WriteAllText(Path.Combine(Root, UngrepConfig.RelativePath), json);
    }

    private string Payload(string tool, object? input, string? session = null, string? cwd = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["session_id"] = session ?? "case-" + Interlocked.Increment(ref _sessions),
            ["cwd"] = cwd ?? Root.Replace('\\', '/'),
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = tool,
            ["tool_input"] = input,
        });

    private string? Handle(string payload, string? root = null) =>
        new GuardsHandler().Handle(HookInput.Parse("PreToolUse", payload), UnityProject.Find(root ?? Root)!, CancellationToken.None);

    private string Dispatch(string payload)
    {
        var output = new StringWriter();
        new HookDispatcher(HookHandlers.All).Run("PreToolUse", new StringReader(payload), output, Root);
        return output.ToString();
    }

    private static object Edit(string file, string old, string replacement, bool all = false) =>
        new { file_path = file, old_string = old, new_string = replacement, replace_all = all };

    private static object Shell(string command) => new { command, description = "test" };

    // The note text of a warn, `DENY` and the reason of a deny, `(silent)` for no output.
    private static string Note(string? output)
    {
        if (output is null)
        {
            return "(silent)";
        }

        using JsonDocument doc = JsonDocument.Parse(output);
        JsonElement specific = doc.RootElement.GetProperty("hookSpecificOutput");
        return specific.TryGetProperty("additionalContext", out JsonElement context)
            ? context.GetString()!
            : "DENY " + specific.GetProperty("permissionDecisionReason").GetString();
    }
}
