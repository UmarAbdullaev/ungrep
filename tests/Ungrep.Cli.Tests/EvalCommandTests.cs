using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ungrep.EditorLink.Tests;

namespace Ungrep.Cli.Tests;

/// <summary>
/// `ug eval` against a fake bridge (SPEC-editor-link, Running code): what it compiles against, the
/// request and body it sends, compile errors and refusals that send nothing, `--check`, batch steps.
/// </summary>
[Collection(nameof(EditorCommandTests))]
public sealed class EvalCommandTests : IDisposable
{
    private const string Ran = "{\"lines\":[\"ran snippet in 0.4 ms\",\"  result: 2\"]}";

    private static readonly Lazy<string> _refs = new(RefsAnswer);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-eval-cli-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeBridge _bridge = new();

    public EvalCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets"));
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));

        // No editor of this version is installed, so the folder fallback finds no Unity assemblies.
        File.WriteAllText(Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2000.1.0f1\n");
        EditorCommands.ProcessCheck = _ => true;
    }

    public void Dispose()
    {
        EditorCommands.ProcessCheck = null;
        _bridge.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void An_expression_is_compiled_here_and_sent_with_its_pdb()
    {
        Bridge(_ => (200, Ran));

        Assert.Equal("ran snippet in 0.4 ms\n  result: 2\n", Run("eval", "1 + 1").Output);
        Assert.Equal(["/status", "/eval/refs", "/eval?source=snippet&what=snippet&entry=__UgSnippet.Run&n=0&timeout=60"], _bridge.Requests);
        byte[] body = Assert.Single(_bridge.Bodies);
        int length = BinaryPrimitives.ReadInt32LittleEndian(body);
        Assert.Equal((byte)'M', body[4]);
        Assert.Equal((byte)'Z', body[5]);
        Assert.True(body.Length > 4 + length, "the PDB follows the assembly");
        Assert.Equal("BSJB"u8.ToArray(), body[(4 + length)..(4 + length + 4)]);
    }

    [Fact]
    public void A_file_runs_its_entry_with_the_arguments()
    {
        Bridge(_ => (200, "{\"lines\":[\"ran Build.Arena(int waves, string level) in 41 ms\"]}"));
        string file = Path.Combine(_root, "Build.cs");
        File.WriteAllText(file, "public static class Build { public static string Arena(int waves, string level) => level + waves; }");

        Assert.Equal("ran Build.Arena(int waves, string level) in 41 ms\n", Run("eval", "--file", file, "--entry", "Build.Arena", "3", "Hard").Output);
        string source = Uri.EscapeDataString(file.Replace('\\', '/'));
        Assert.Equal("/eval?source=" + source + "&entry=Build.Arena&n=2&a0=3&a1=Hard&timeout=60", _bridge.Requests.Last());

        File.WriteAllText(file, "static class Tool { static int Run(int x) => x; }");
        Run("eval", "--file", file, "7", "--timeout", "5");
        Assert.Equal("/eval?source=" + source + "&entry=Tool.Run&n=1&a0=7&timeout=5", _bridge.Requests.Last());
    }

    [Fact]
    public void Compile_errors_and_refusals_send_nothing()
    {
        Bridge(_ => (200, Ran));

        Assert.Equal(
            "did not run snippet: 1 compile error\n  snippet:1:12 CS0117 'GameObject' does not contain a definition for 'Nope'\n",
            Run("eval", "GameObject.Nope").Output);
        Assert.Equal("did not run snippet: its statements take no arguments; arguments go to a file's entry method (--file x.cs --entry Type.Method)\n", Run("eval", "var a = 1;", "2").Output);
        Assert.Equal("did not run snippet: the code has statements, which run as its entry; --entry is for a file of types\n", Run("eval", "1 + 1", "--entry", "A.Run").Output);
        Assert.Equal("give the code: ug eval \"Selection.count\", or ug eval --file x.cs\n", Run("eval").Output);
        Assert.StartsWith("cannot read nowhere.cs: ", Run("eval", "--file", "nowhere.cs").Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_bridge.Requests, r => r.StartsWith("/eval?", StringComparison.Ordinal));
    }

    [Fact]
    public void More_than_ten_errors_are_cut_and_written_whole()
    {
        Bridge(_ => (200, Ran));

        string output = Run("eval", string.Join(";", Enumerable.Range(1, 12).Select(i => "Missing" + i + "()"))).Output;

        string[] lines = output.TrimEnd('\n').Split('\n');
        Assert.Equal("did not run snippet: 12 compile errors", lines[0]);
        Assert.Equal(12, lines.Length);
        Assert.Equal("  (+2 more: Library/ungrep/eval/errors.txt)", lines[^1]);
        Assert.Equal(12, File.ReadAllLines(Path.Combine(_root, EditorCommands.ErrorsFile)).Length);
    }

    [Fact]
    public void Check_compiles_without_sending_and_without_Unity()
    {
        Bridge(_ => (200, Ran));
        Assert.Matches(@"^compiled: no errors \(\d+ ms\)\n$", Run("eval", "--check", "Selection.count").Output);
        Assert.Equal(["/status", "/eval/refs"], _bridge.Requests);

        // Unity closed: the folders, here without any Unity assembly, so UnityEngine is missing.
        File.Delete(Path.Combine(_root, "Library", "ungrep", "bridge.json"));
        Assert.StartsWith("snippet: ", Run("eval", "--check", "Selection.count").Output, StringComparison.Ordinal);
        Assert.Equal(
            "Unity not reachable (no bridge.json: Unity is closed or the bridge is not installed); ug eval needs the bridge (ug eval --check compiles without it)\n",
            Run("eval", "1").Output.Split('\n')[0] + "\n");
    }

    [Fact]
    public void A_bridge_older_than_050_is_too_old_and_its_answers_are_printed()
    {
        _bridge.Publish(_root, bridge: "0.4.0");
        _bridge.Handle = path => path == "/status" ? (200, FakeBridge.Status("idle", 3)) : (404, "{\"error\":\"unknown request\"}");
        Assert.Equal("bridge 0.4.0 is too old for ug eval: run ug bridge install again\n", Run("eval", "1").Output);

        // A 0.5.0 bridge from before ug eval: no /eval/refs; --check compiles against the folders still.
        _bridge.Publish(_root, bridge: "0.5.0");
        Assert.Equal("bridge 0.5.0 is too old for ug eval: run ug bridge install again\n", Run("eval", "1").Output);
        Assert.StartsWith("snippet: ", Run("eval", "--check", "Selection.count").Output, StringComparison.Ordinal);

        Bridge(path => path.StartsWith("/eval?", StringComparison.Ordinal)
            ? (200, "{\"error\":\"snippet threw after 3 ms: InvalidOperationException: boom\\n  at snippet:2\"}")
            : (500, "{\"error\":\"boom\"}"));
        Assert.Equal("snippet threw after 3 ms: InvalidOperationException: boom\n  at snippet:2\n", Run("eval", "throw new InvalidOperationException(\"boom\");").Output);

        // Refused by the bridge (over its body cap): it did not run, unlike no answer at all.
        Bridge(_ => (413, "{\"error\":\"the body is over 16 MB\"}"));
        Assert.Equal("did not run snippet: bridge answered 413: the body is over 16 MB\n", Run("eval", "1").Output);
    }

    [Theory]
    [InlineData("eval Selection.count", "eval|--|Selection.count")]
    [InlineData("ug eval \"var a = 1; a\"", "eval|--|var a = 1; a")]
    [InlineData("eval --timeout 5 --check while (true) { }", "eval|--timeout|5|--check|--|while (true) { }")]
    [InlineData("eval --timeout=5 \"x\" + \"y\"", "eval|--timeout=5|--|\"x\" + \"y\"")]
    [InlineData("eval \"Debug.Log(\\\"hi\\\")\"", "eval|--|Debug.Log(\"hi\")")]
    [InlineData("eval --count; --x", "eval|--|--count; --x")]
    [InlineData("eval --file Tools/Build.cs 3", null)]
    [InlineData("tree Player", null)]
    public void A_batch_eval_step_takes_the_rest_of_its_line_as_the_code(string line, string? words)
    {
        Assert.Equal(words, EditorCommands.EvalStep(line) is { } step ? string.Join('|', step) : null);
    }

    [Fact]
    public void A_batch_runs_eval_steps_over_its_one_connection()
    {
        Bridge(path => path.StartsWith("/eval?", StringComparison.Ordinal) ? (200, Ran) : (200, "{\"lines\":[\"Main  1 root\"]}"));

        string output = Run("batch", "eval var n = 1; n + 1", "tree").Output;

        Assert.StartsWith("1/2 eval var n = 1; n + 1\nran snippet in 0.4 ms\n  result: 2\n2/2 tree\nMain  1 root\n", output, StringComparison.Ordinal);
        Assert.Equal(["/status", "/eval/refs", "/eval?source=snippet&what=snippet&entry=__UgSnippet.Run&n=0&timeout=60", "/tree"], _bridge.Requests);
    }

    // A 0.5.0 bridge: idle, its refs the runtime's and a stub of Unity's namespaces; handle answers the rest.
    private void Bridge(Func<string, (int, string)> handle)
    {
        _bridge.Publish(_root, bridge: "0.5.0");
        _bridge.Handle = path => path switch
        {
            "/status" => (200, FakeBridge.Status("idle", 3)),
            "/eval/refs" => (200, _refs.Value),
            _ => handle(path),
        };
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run([.. args, "--project", _root], output, error);
        Assert.Equal("", error.ToString());
        return (exitCode, output.ToString().ReplaceLineEndings("\n"));
    }

    // The .NET runtime's assemblies and a stub with the namespaces and types the default usings name.
    private static string RefsAnswer()
    {
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        List<string> refs = [.. Directory.GetFiles(runtime, "*.dll").Where(f => (Path.GetFileName(f).StartsWith("System", StringComparison.Ordinal) || Path.GetFileName(f) is "mscorlib.dll" or "netstandard.dll") && IsManaged(f))];
        string stub = Path.Combine(Path.GetTempPath(), "ug-eval-stub-" + Guid.NewGuid().ToString("N")[..8] + ".dll");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "UnityStub",
            [CSharpSyntaxTree.ParseText("""
                namespace UnityEngine { public class Object { } public static class Random { } public class GameObject : Object { } }
                namespace UnityEngine.SceneManagement { public static class SceneManager { } }
                namespace UnityEditor { public static class Selection { public static int count => 2; } }
                """)],
            refs.Select(r => MetadataReference.CreateFromFile(r)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.True(compilation.Emit(stub).Success);
        refs.Add(stub);
        return JsonSerializer.Serialize(new { refs, defines = new[] { "UNITY_EDITOR" }, lang = "9.0" });
    }

    private static bool IsManaged(string file)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(file));
        return pe.HasMetadata;
    }
}
