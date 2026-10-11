using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnityGraph.Code.Tests;

/// <summary>
/// `ug eval`'s compiler (SPEC-editor-link, Running code) against a stub of the Unity names it
/// imports by default; the built snippets are loaded and run here, as the bridge runs them.
/// </summary>
public sealed class SnippetCompilerTests
{
    private const string StubSource = """
        namespace UnityEngine
        {
            public class Object { public string name; }
            public static class Random { public static int Range(int min, int max) => min; }
            public static class Debug
            {
                public static readonly System.Collections.Generic.List<string> Logs = new System.Collections.Generic.List<string>();
                public static void Log(object message) => Logs.Add(message?.ToString());
            }
            public class GameObject : Object { public GameObject(string name) { this.name = name; } }
        }
        namespace UnityEditor
        {
            public static class Selection { public static int count = 2; }
        }
        namespace UnityEngine.SceneManagement
        {
            public static class SceneManager { public static int sceneCount = 1; }
        }
        namespace UnityGraph.Bridge
        {
            public static class EvalGuard
            {
                public static int Calls;
                public static int Limit = int.MaxValue;
                public static double Seconds;
                public static void Check(double seconds)
                {
                    Seconds = seconds;
                    if (++Calls > Limit) throw new System.TimeoutException("a loop kept the editor waiting");
                }
            }
        }
        """;

    private static readonly Lazy<(string Path, Assembly Assembly)> _stubs = new(BuildStubs);

    internal static SnippetSettings Settings(params string[] defines) =>
        new([.. Runtime(), _stubs.Value.Path], defines, "9.0") { GuardSeconds = 5 };

    [Theory]
    [InlineData("Selection.count + 1", 3)]
    [InlineData("var go = new GameObject(\"a\"); go.name.Length", 1)]
    [InlineData("var a = 4; a", 4)]
    [InlineData("var a = 2;\nreturn a * 3", 6)]
    [InlineData("var a = 2; return a * 4;", 8)]
    [InlineData("class Helper { public static int Twice(int x) => x * 2; }\nHelper.Twice(4)", 8)]
    [InlineData("int Sq(int x) => x * x;\nSq(5)", 25)]
    [InlineData("static int Cube(int x) => x * x * x;\nCube(2)", 8)]
    [InlineData("Object o = null; Random.Range(7, 9)", 7)]
    [InlineData("using Object = System.Object;\nObject o = 9; o", 9)]
    [InlineData("SceneManager.sceneCount + Enumerable.Range(0, 3).Count()", 4)]
    [InlineData("Func<Task> later = async () => await Task.Delay(1); 3", 3)]
    public void Statements_and_expressions_give_their_value(string code, int expected)
    {
        SnippetBuild build = Build(code);

        Assert.True(build.Statements);
        Assert.True(build.HasResult);
        Assert.Equal(expected, Run(build));
    }

    [Theory]
    [InlineData("Debug.Log(\"hi\")")]
    [InlineData("Debug.Log(\"hi\");")]
    [InlineData("var x = 0; Debug.Log(\"hi\"); x = 5")]
    [InlineData("var x = 0; Debug.Log(\"hi\"); x++;")]
    [InlineData("Debug.Log(\"hi\"); return;")]
    public void Void_calls_assignments_and_bare_returns_give_no_value(string code)
    {
        SnippetBuild build = Build(code);

        Assert.False(build.HasResult);
        Assert.Null(Run(build));
        Assert.Contains("hi", StubLogs());
    }

    [Fact]
    public async Task Await_makes_an_awaited_task_and_yield_a_stepped_enumerator()
    {
        object? task = Run(Build("await Task.Delay(1);\nreturn 5;"));
        Assert.Equal(5, await Assert.IsAssignableFrom<Task<object>>(task));

        object? quiet = Run(Build("await Task.Delay(1);\nDebug.Log(\"hi\")"));
        Assert.IsNotType<Task<object>>(quiet);
        await Assert.IsAssignableFrom<Task>(quiet);

        var steps = Assert.IsAssignableFrom<IEnumerator>(Run(Build("yield return null;\nyield return 1;")));
        int count = 0;
        while (steps.MoveNext())
        {
            count++;
        }

        Assert.Equal(2, count);
    }

    [Fact]
    public void Defines_pick_the_editor_branch()
    {
        const string code = "#if UNITY_EDITOR\nreturn 1;\n#else\nreturn 2;\n#endif";

        Assert.Equal(1, Run(Build(code, "UNITY_EDITOR")));
        Assert.Equal(2, Run(Build(code)));
    }

    [Fact]
    public void A_file_of_types_starts_from_its_one_Run_or_Main_or_the_entry_given()
    {
        SnippetBuild run = Build("static class Tools { static int Run() => 7; }");
        Assert.False(run.Statements);
        Assert.Equal(("Tools", "Run"), (run.EntryType, run.EntryMethod));
        Assert.Equal(7, Run(run));

        const string nested = "namespace N { class Outer { class Inner { static int Go(int a) => a; } static void Main() { } } }";
        SnippetBuild entry = SnippetCompiler.Compile(nested, "x.cs", Settings() with { Entry = "Inner.Go" });
        Assert.True(entry.Ok);
        Assert.Equal(("N.Outer+Inner", "Go"), (entry.EntryType, entry.EntryMethod));
        Assert.Equal("N.Outer", SnippetCompiler.Compile(nested, "x.cs", Settings()).EntryType);
    }

    [Theory]
    [InlineData("class A { static void Helper() { } }", null, "no static Run() or Main(); give --entry Type.Method")]
    [InlineData("class A { static void Run() { } } class B { static void Main() { } }", null, "several entries: A.Run, B.Main; give --entry Type.Method")]
    [InlineData("class A { static void Run() { } }", "B.Run", "no type B in the code")]
    [InlineData("class A { static void Run() { } }", "A.Go", "no static method Go on A")]
    [InlineData("class A { static void Run() { } }", "Run", "--entry must be Type.Method")]
    [InlineData("Debug.Log(1);", "A.Run", "the code has statements, which run as its entry; --entry is for a file of types")]
    [InlineData("  \n ", null, "no code")]
    public void Nothing_to_run_is_a_problem_not_an_error(string code, string? entry, string problem)
    {
        SnippetBuild build = SnippetCompiler.Compile(code, "x.cs", Settings() with { Entry = entry });

        Assert.Equal(problem, build.Problem);
        Assert.Null(build.Assembly);
    }

    [Fact]
    public void Errors_name_the_codes_own_lines_and_columns()
    {
        SnippetBuild build = SnippetCompiler.Compile("var a = 1;\nvar b = a.Nope;\nclass T { int X => Missing; }\nb", "Tools/x.cs", Settings());

        Assert.Null(build.Assembly);
        Assert.Collection(
            build.Errors,
            e => Assert.Equal((2, 11, "CS1061"), (e.Line, e.Column, e.Code)),
            e => Assert.Equal((3, 20, "CS0103"), (e.Line, e.Column, e.Code)));
        Assert.Contains("'Missing'", build.Errors[1].Message, StringComparison.Ordinal);

        SnippetError bare = Assert.Single(Build("Selection.nope", expectOk: false).Errors);
        Assert.Equal((1, 11, "CS0117"), (bare.Line, bare.Column, bare.Code));

        SnippetError indented = Assert.Single(Build("var a = 1;\n    a.Nope", expectOk: false).Errors);
        Assert.Equal((2, 7), (indented.Line, indented.Column));
    }

    [Fact]
    public void A_missing_semicolon_is_allowed_on_the_last_statement_only()
    {
        Assert.Equal(3, Run(Build("var a = 1\n;var b = 2; a + b")));
        SnippetError error = Assert.Single(Build("var a = 1\nvar b = 2; b", expectOk: false).Errors);
        Assert.Equal((1, 10, "CS1002"), (error.Line, error.Column, error.Code));
    }

    [Fact]
    public void Frames_name_the_snippet_line()
    {
        SnippetBuild build = Build("var a = 1;\n\nthrow new InvalidOperationException(\"boom\" + a);");

        var thrown = Assert.Throws<InvalidOperationException>(() => Run(build));
        Assert.Contains("in snippet:line 3", thrown.StackTrace, StringComparison.Ordinal);
    }

    [Fact]
    public void Loops_call_the_guard_first_and_it_can_stop_them()
    {
        Type guard = _stubs.Value.Assembly.GetType("UnityGraph.Bridge.EvalGuard")!;
        lock (guard)
        {
            guard.GetField("Calls")!.SetValue(null, 0);
            Assert.Equal(3, Run(Build("int n = 0;\nfor (int i = 0; i < 3; i++) n++;\nforeach (var x in new[] { 1 }) { }\nn")));
            Assert.Equal(4, guard.GetField("Calls")!.GetValue(null));
            Assert.Equal(5d, guard.GetField("Seconds")!.GetValue(null));

            guard.GetField("Calls")!.SetValue(null, 0);
            guard.GetField("Limit")!.SetValue(null, 10);
            try
            {
                var stopped = Assert.Throws<TimeoutException>(() => Run(Build("int n = 0;\nwhile (true) { n++; }")));
                Assert.Contains("in snippet:line 2", stopped.StackTrace, StringComparison.Ordinal);
                Assert.Throws<TimeoutException>(() => Run(Build("do Debug.Log(1); while (true);")));
            }
            finally
            {
                guard.GetField("Limit")!.SetValue(null, int.MaxValue);
            }
        }
    }

    [Fact]
    public void Without_a_guard_seconds_loops_stay_as_written()
    {
        SnippetBuild build = SnippetCompiler.Compile("int n = 0; while (n < 2) n++; n", "snippet", Settings() with { GuardSeconds = 0 });

        Assert.Equal(2, Run(build));
    }

    [Fact]
    public void Check_only_compiles()
    {
        SnippetBuild build = SnippetCompiler.Compile("Selection.count", "snippet", Settings(), emit: false);

        Assert.True(build.Ok);
        Assert.Null(build.Assembly);
        Assert.True(build.HasResult);
    }

    [Fact]
    public void A_reference_rewritten_by_a_script_compile_is_read_again()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ug-snippet-refs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string game = Path.Combine(folder, "Game.dll");
        try
        {
            Library(game, "public static class Game { public static int A => 1; }");
            SnippetSettings settings = Settings() with { References = [.. Settings().References, game] };
            Assert.True(SnippetCompiler.Compile("Game.A", "snippet", settings, emit: false).Ok);
            Assert.Equal("CS0117", SnippetCompiler.Compile("Game.B", "snippet", settings, emit: false).Errors.Single().Code);

            Library(game, "public static class Game { public static int B => 2; }");
            File.SetLastWriteTimeUtc(game, DateTime.UtcNow.AddMinutes(1));

            Assert.True(SnippetCompiler.Compile("Game.B", "snippet", settings, emit: false).Ok);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("6000.3.14f1", "9.0")]
    [InlineData("2022.3.62f1", "9.0")]
    [InlineData("2021.2.0f1", "9.0")]
    [InlineData("2021.1.28f1", "8.0")]
    [InlineData("2020.2.7f1", "8.0")]
    [InlineData("2019.4.40f1", "7.3")]
    [InlineData("", "7.3")]
    public void Language_follows_the_Unity_version(string unity, string language) =>
        Assert.Equal(language, SnippetCompiler.LanguageFor(unity));

    private static SnippetBuild Build(string code, params string[] defines) => Build(code, true, defines);

    private static SnippetBuild Build(string code, bool expectOk, params string[] defines)
    {
        SnippetBuild build = SnippetCompiler.Compile(code, "snippet", Settings(defines));
        if (expectOk)
        {
            Assert.True(build.Ok, build.Problem ?? string.Join("\n", build.Errors));
        }

        return build;
    }

    internal static object? Run(SnippetBuild build)
    {
        _ = _stubs.Value;
        Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(build.Assembly!), new MemoryStream(build.Pdb!));
        MethodInfo method = assembly.GetType(build.EntryType!, throwOnError: true)!
            .GetMethod(build.EntryMethod!, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

        // The bridge prints a result when the entry returns one, so statements without a value must be void.
        if (build.Statements && (method.ReturnType == typeof(object) || method.ReturnType == typeof(void)))
        {
            Assert.Equal(build.HasResult, method.ReturnType == typeof(object));
        }

        try
        {
            return method.Invoke(null, null);
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private static List<string> StubLogs() =>
        (List<string>)_stubs.Value.Assembly.GetType("UnityEngine.Debug")!.GetField("Logs")!.GetValue(null)!;

    private static IEnumerable<string> Runtime() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Where(p => !p.Contains("CodeAnalysis", StringComparison.Ordinal));

    private static (string, Assembly) BuildStubs()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ug-snippet-stubs-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "UnityStubs.dll");
        Library(path, StubSource);
        return (path, AssemblyLoadContext.Default.LoadFromAssemblyPath(path));
    }

    private static void Library(string path, string source)
    {
        CSharpCompilation library = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(path),
            [CSharpSyntaxTree.ParseText(source)],
            Runtime().Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using FileStream file = File.Create(path);
        Microsoft.CodeAnalysis.Emit.EmitResult result = library.Emit(file);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
    }
}
