using UnityGraph.EditorLink;
using UnityGraph.EditorLink.Tests;

namespace UnityGraph.Cli.Tests;

/// <summary>
/// `ug api` / `ug docs` answers that name a documentation page (local file, cache, the docs.unity3d.com
/// fallback with a fake fetch) and `ug batch` (one connection, steps checked first, stop at the
/// first failure), against a fake bridge. No test here uses the network.
/// </summary>
[Collection(nameof(EditorCommandTests))]
public sealed class EditorDocsAndBatchTests : IDisposable
{
    private const string PageUrl = "https://docs.unity3d.com/6000.3/Documentation/Manual/Prefabs.html";
    private const string PageHtml = "<html><body><nav>menu</nav><div class=\"content\"><h1>Prefabs</h1><p>Reusable assets.</p></div></body></html>";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-docs-cli-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeBridge _bridge = new();
    private readonly List<string> _fetches = [];
    private TextReader? _stdin;

    public EditorDocsAndBatchTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.14f1\n");
        EditorCommands.ProcessCheck = _ => true;
        EditorCommands.DocsCacheOverride = new DocsCache(Path.Combine(_root, "docs-cache"));
        EditorCommands.DocsFetchOverride = url =>
        {
            _fetches.Add(url);
            return url == PageUrl ? new DocsFetchResult(PageHtml, null) : new DocsFetchResult(null, "HTTP 404 for " + url);
        };
        _bridge.Publish(_root, bridge: "0.2.0");
    }

    public void Dispose()
    {
        EditorCommands.ProcessCheck = null;
        EditorCommands.DocsCacheOverride = null;
        EditorCommands.DocsFetchOverride = null;
        if (_stdin != null)
        {
            Console.SetIn(_stdin);
        }

        _bridge.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_local_documentation_page_is_trimmed_and_printed_without_a_fetch()
    {
        string file = Path.Combine(_root, "Prefabs.html");
        File.WriteAllText(file, PageHtml);
        Answer(_ => new Json("local", file).Text);

        (int exitCode, string output) = Run("docs", "Manual/Prefabs");

        Assert.Equal(0, exitCode);
        Assert.Equal("# Prefabs\n\nReusable assets.\n", output);
        Assert.Equal(["/status", "/api?page=Manual%2FPrefabs"], _bridge.Requests);
        Assert.Empty(_fetches);
    }

    [Fact]
    public void An_unknown_type_falls_back_to_the_online_page_then_the_cache()
    {
        Answer(_ => Online("Manual/Prefabs is not in the local documentation", PageUrl));

        string first = Run("docs", "Prefabs").Output;
        string second = Run("docs", "Prefabs").Output;

        Assert.Equal("# Prefabs\n\nReusable assets.\n", first);
        Assert.Equal(first, second);
        Assert.Equal([PageUrl], _fetches);
    }

    [Fact]
    public void Docs_online_false_stops_the_fetch_and_names_the_page()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".claude"));
        File.WriteAllText(Path.Combine(_root, ".claude", "unitygraph.json"), "{\"docs\":{\"online\":false}}");
        Answer(_ => Online("no type Foo in the loaded assemblies", PageUrl));

        Assert.Equal(
            "no type Foo in the loaded assemblies\ndocs.online is off in .claude/unitygraph.json; read it at:\n  " + PageUrl + "\n",
            Run("api", "Foo").Output);
        Assert.Empty(_fetches);
    }

    [Fact]
    public void A_page_that_cannot_be_fetched_says_why_and_still_exits_zero()
    {
        const string Missing = "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Foo.html";
        Answer(_ => Online("no type Foo in the loaded assemblies", Missing));

        (int exitCode, string output) = Run("api", "Foo");

        Assert.Equal(0, exitCode);
        Assert.Equal("no type Foo in the loaded assemblies\nnot fetched, read it at:\n  " + Missing + " (HTTP 404 for " + Missing + ")\n", output);
    }

    [Fact]
    public void Reflection_answers_are_printed_as_lines_and_never_reach_the_docs_code()
    {
        Answer(_ => "{\"lines\":[\"class UnityEngine.Transform  (UnityEngine.CoreModule)\"]}");

        Assert.Equal("class UnityEngine.Transform  (UnityEngine.CoreModule)\n", Run("api", "Transform").Output);
        Assert.Empty(_fetches);
    }

    [Fact]
    public void A_batch_runs_its_steps_in_order_over_one_connection()
    {
        Answer(path => path switch
        {
            "/object/create?name=Crate" => "{\"lines\":[\"created Crate\"]}",
            "/set?object=Crate&field=speed&value=3" => "{\"lines\":[\"Crate.speed 0 -> 3\"]}",
            _ => "{\"error\":\"unexpected " + path + "\"}",
        });

        (int exitCode, string output) = Run("batch", "object create Crate", "ug set Crate speed 3");

        Assert.Equal(0, exitCode);
        Assert.Equal("""
            1/2 object create Crate
            created Crate
            2/2 ug set Crate speed 3
            Crate.speed 0 -> 3
            2 steps done. Each step is its own undo group (ug undo reverts the last).

            """.ReplaceLineEndings("\n"), output);
        Assert.Equal(["/status", "/object/create?name=Crate", "/set?object=Crate&field=speed&value=3"], _bridge.Requests);
    }

    [Fact]
    public void Quoted_words_stay_together()
    {
        Answer(_ => "{\"lines\":[\"ok\"]}");

        Run("batch", "object rename Player \"Hero One\"");

        Assert.Equal(["/status", "/object/rename?object=Player&name=Hero%20One"], _bridge.Requests);
    }

    [Fact]
    public void A_refusal_stops_the_batch_and_says_what_did_not_run()
    {
        Answer(path => path.StartsWith("/component/add", StringComparison.Ordinal)
            ? "{\"error\":\"not found: Ghost in the loaded scenes\"}"
            : "{\"lines\":[\"done\"]}");

        (int exitCode, string output) = Run("batch", "object create A", "component add Ghost Rigidbody", "object create B", "save");

        Assert.Equal(0, exitCode);
        Assert.Equal("""
            1/4 object create A
            done
            2/4 component add Ghost Rigidbody
            not found: Ghost in the loaded scenes
            stopped at step 2: 2 steps not run. Earlier steps stay done; ug undo reverts them one by one.

            """.ReplaceLineEndings("\n"), output);
        Assert.Equal(["/status", "/object/create?name=A", "/component/add?object=Ghost&type=Rigidbody"], _bridge.Requests);
    }

    [Fact]
    public void A_run_step_that_throws_stops_the_batch()
    {
        _bridge.Publish(_root, bridge: "0.5.0");
        Answer(path => path.StartsWith("/run", StringComparison.Ordinal)
            ? "{\"error\":\"Game.Tools.Spawn(int count) threw after 2 ms: ArgumentException: count must be positive\\n  at Assets/Tools.cs:12\"}"
            : "{\"lines\":[\"done\"]}");

        (int exitCode, string output) = Run("batch", "object create A", "run Game.Tools.Spawn -1", "save");

        Assert.Equal(0, exitCode);
        Assert.Equal("""
            1/3 object create A
            done
            2/3 run Game.Tools.Spawn -1
            Game.Tools.Spawn(int count) threw after 2 ms: ArgumentException: count must be positive
              at Assets/Tools.cs:12
            stopped at step 2: 1 step not run. Earlier steps stay done; ug undo reverts them one by one.

            """.ReplaceLineEndings("\n"), output);
        Assert.Equal(["/status", "/object/create?name=A", "/run?target=Game.Tools.Spawn&n=1&a0=-1&timeout=60"], _bridge.Requests);
    }

    [Fact]
    public void A_vendored_edit_stops_the_batch_before_asking_Unity()
    {
        Answer(_ => "{\"lines\":[\"done\"]}");

        string output = Run("batch", "object delete Assets/Plugins/Kit.prefab:Root", "save").Output;

        Assert.Contains("1/2 object delete Assets/Plugins/Kit.prefab:Root\nAssets/Plugins/Kit.prefab:Root is in the vendored folder Assets/Plugins/, which ug does not edit\nstopped at step 1: 1 step not run.", output, StringComparison.Ordinal);
        Assert.Equal(["/status"], _bridge.Requests);
    }

    [Fact]
    public void A_docs_page_that_cannot_be_found_stops_the_batch()
    {
        Answer(path => path.StartsWith("/api", StringComparison.Ordinal) ? Online("no type Foo", "https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Foo.html") : "{\"lines\":[\"done\"]}");

        string output = Run("batch", "api Foo", "save").Output;

        Assert.Contains("stopped at step 1: 1 step not run.", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lost_answer_stops_the_batch()
    {
        Serve(path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (500, "{\"error\":\"boom\"}"));

        string output = Run("batch", "tree", "save").Output;

        Assert.Equal("""
            1/2 tree
            no answer from Unity (bridge answered 500: boom)
            stopped at step 1: 1 step not run. Earlier steps stay done; ug undo reverts them one by one.

            """.ReplaceLineEndings("\n"), output);
    }

    [Theory]
    [InlineData("find --live --tag Player", "not an editor-ops command")]
    [InlineData("run Tools.Spawn --timeout soon", "Cannot parse argument")]
    [InlineData("compile", "not an editor-ops command")]
    [InlineData("status", "not an editor-ops command")]
    [InlineData("batch \"tree\"", "not an editor-ops command")]
    [InlineData("query Player", "not an editor-ops command")]
    [InlineData("set Player", "Required argument missing")]
    [InlineData("tree --depth many", "Cannot parse argument")]
    [InlineData("scene bogus", "Unrecognized command or argument")]
    public void Every_step_is_checked_before_the_first_runs(string bad, string message)
    {
        Answer(_ => "{\"lines\":[\"done\"]}");

        (int exitCode, string output) = Run("batch", "object create A", bad);

        Assert.Equal(1, exitCode);
        Assert.Contains("step 2 (" + bad + "): ", output, StringComparison.Ordinal);
        Assert.Contains(message, output, StringComparison.Ordinal);
        Assert.EndsWith("nothing ran\n", output, StringComparison.Ordinal);
        Assert.Empty(_bridge.Requests);
    }

    [Fact]
    public void Steps_come_from_a_file_with_comments_and_blank_lines()
    {
        Answer(_ => "{\"lines\":[\"done\"]}");
        string file = Path.Combine(_root, "steps.txt");
        File.WriteAllText(file, "# set the scene up\nobject create A\n\n   \n  # more\nsave\n");

        (int exitCode, string output) = Run("batch", "--file", file);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("1/2 object create A\ndone\n2/2 save\ndone\n2 steps done.", output, StringComparison.Ordinal);
        Assert.Equal(["/status", "/object/create?name=A", "/save"], _bridge.Requests);
    }

    [Fact]
    public void Steps_come_from_standard_input()
    {
        Answer(_ => "{\"lines\":[\"done\"]}");
        _stdin = Console.In;
        Console.SetIn(new StringReader("object create A\nsave\n"));

        (int exitCode, string output) = Run("batch", "--file", "-");

        Assert.Equal(0, exitCode);
        Assert.Contains("2 steps done.", output, StringComparison.Ordinal);
        Assert.Equal(["/status", "/object/create?name=A", "/save"], _bridge.Requests);
    }

    [Fact]
    public void A_batch_with_no_steps_is_a_usage_error()
    {
        (int exitCode, string output) = Run("batch");

        Assert.Equal(1, exitCode);
        Assert.StartsWith("give steps as arguments or with --file", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_file_is_a_usage_error()
    {
        (int exitCode, string output) = Run("batch", "--file", Path.Combine(_root, "missing.txt"));

        Assert.Equal(1, exitCode);
        Assert.StartsWith("cannot read ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_the_bridge_a_batch_says_it_needs_it()
    {
        File.Delete(BridgeClient.DiscoveryPath(_root));

        (int exitCode, string output) = Run("batch", "save");

        Assert.Equal(0, exitCode);
        Assert.StartsWith("Unity not reachable (no bridge.json: Unity is closed or the bridge is not installed); ug batch needs the bridge\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_command_after_a_batch_connects_on_its_own_again()
    {
        Answer(_ => "{\"lines\":[\"done\"]}");

        Run("batch", "save");
        Run("save");

        Assert.Equal(["/status", "/save", "/status", "/save"], _bridge.Requests);
    }

    private static string Online(string error, params string[] urls) =>
        "{\"error\":\"" + error + "\",\"online\":[" + string.Join(",", urls.Select(u => "\"" + u + "\"")) + "]}";

    // A 0.2.0 bridge answering every non-status request with handle's JSON.
    private void Answer(Func<string, string> handle) => Serve(path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (200, handle(path)));

    private void Serve(Func<string, (int, string)> handle) => _bridge.Handle = handle;

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run([.. args, "--project", _root], output, error);
        return (exitCode, (output.ToString() + error).ReplaceLineEndings("\n"));
    }

    private sealed class Json(string name, string value)
    {
        public string Text { get; } = "{\"" + name + "\":" + System.Text.Json.JsonSerializer.Serialize(value) + "}";
    }
}
