using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGraph.Bridge.Tests
{
    public enum RunMode
    {
        Slow,
        Fast,
    }

    [Flags]
    public enum RunFlags
    {
        None = 0,
        Red = 1,
        Blue = 2,
    }

    public class RunShape
    {
        public int width;

        public int Height { get; set; }

        public int Area => width * Height;
    }

    public class RunTargets
    {
        public static int Calls;

        internal static ConsoleBuffer Console;

        public static string Hello() => "hello";

        public static void Touch() => Calls++;

        public static void Boom() => throw new InvalidOperationException("boom\nsecond line");

        public static string Echo(string text) => text;

        public static int Sum(int a, int b = 10) => a + b;

        public static string Pick(int value) => "int";

        public static string Pick(float value) => "float";

        public static string Pick(string value) => "string";

        public static string Tie(int value) => "int";

        public static string Tie(long value) => "long";

        public static int Count(string label, params int[] values) => values.Length;

        public static int Total(List<int> values) => values.Sum();

        public static string Place(Vector3 at, Color color, RunMode mode, RunFlags flags) => at + " " + color + " " + mode + " " + flags;

        public static int Area(RunShape shape) => shape.Area;

        public static bool Split(string text, out string head, ref int count)
        {
            head = text.Substring(0, 1);
            count++;
            return true;
        }

        public static void Log()
        {
            Console.Add("log", "first", "Assets/Tools.cs:3");
            Console.Add("log", "first", "Assets/Tools.cs:3");
            Console.Add("warning", "second", null);
        }

        public static int[] Many() => Enumerable.Range(1, 25).ToArray();

        public static GameObject Spawn(string name)
        {
            var made = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(made, "spawn");
            return made;
        }

        public static async Task<int> Later()
        {
            await Task.Yield();
            return 7;
        }

        public static IEnumerator Steps()
        {
            yield return null;
            yield return Inner();
        }

        public static TaskCompletionSource<bool> Pending;

        public static Task Forever() => Pending.Task;

        public static T Generic<T>(T value) => value;

        public string Mine() => "mine";

        private static string Hidden() => "hidden";

        private static IEnumerator Inner()
        {
            yield return null;
        }
    }

    public class ValuesClrTests
    {
        private static object Read(Type type, string text, out int rank)
        {
            string problem = Values.ParseAs(type, text, out object value, out rank);
            Assert.IsNull(problem, problem);
            return value;
        }

        private static string Problem(Type type, string text) => Values.ParseAs(type, text, out _, out _);

        [Test]
        public void Numbers_say_how_exactly_they_fit()
        {
            Assert.AreEqual(3, Read(typeof(int), " 3 ", out int whole));
            Assert.AreEqual(Values.Exact, whole);
            Assert.AreEqual(3f, Read(typeof(float), "3", out int widened));
            Assert.AreEqual(Values.Loose, widened);
            Assert.AreEqual(2.5, Read(typeof(double), "2.5", out int fraction));
            Assert.AreEqual(Values.Exact, fraction);
            Assert.AreEqual("text", Read(typeof(string), "text", out int any));
            Assert.AreEqual(Values.Loosest, any);
            Assert.AreEqual(true, Read(typeof(bool), "1", out int flag));
            Assert.AreEqual(Values.Loose, flag);

            Assert.AreEqual("not a whole number: 2.5", Problem(typeof(int), "2.5"));
            Assert.AreEqual("out of range for byte: 300", Problem(typeof(byte), "300"));
            Assert.AreEqual("not a number: x", Problem(typeof(float), "x"));
            Assert.AreEqual("not true or false: maybe", Problem(typeof(bool), "maybe"));
        }

        [Test]
        public void Enums_take_names_numbers_and_flags()
        {
            Assert.AreEqual(RunMode.Fast, Read(typeof(RunMode), "fast", out int name));
            Assert.AreEqual(Values.Exact, name);
            Assert.AreEqual(RunMode.Fast, Read(typeof(RunMode), "1", out int number));
            Assert.AreEqual(Values.Loose, number);
            Assert.AreEqual(RunFlags.Red | RunFlags.Blue, Read(typeof(RunFlags), "Red|blue", out _));
            Assert.AreEqual("not one of Slow, Fast: slow, fast", Problem(typeof(RunMode), "slow, fast"));
        }

        [Test]
        public void Unity_values_read_as_ug_set_reads_them()
        {
            Assert.AreEqual(new Vector3(1, 2, 3), Read(typeof(Vector3), "1, 2, 3", out _));
            Assert.AreEqual(Color.red, Read(typeof(Color), "#ff0000", out _));
            Assert.AreEqual(new Color32(255, 0, 0, 255), Read(typeof(Color32), "1, 0, 0", out _));
            Assert.AreEqual(90f, ((Quaternion)Read(typeof(Quaternion), "0, 90, 0", out _)).eulerAngles.y, 0.001f);
            Assert.AreEqual("not 3 numbers like 1, 2, 3: 1, 2", Problem(typeof(Vector3), "1, 2"));
        }

        [Test]
        public void Lists_nullables_types_and_parse_methods()
        {
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, (int[])Read(typeof(int[]), "[1, 2, 3]", out _));
            CollectionAssert.AreEqual(new List<int> { 4, 5 }, (List<int>)Read(typeof(List<int>), "4, 5", out _));
            CollectionAssert.AreEqual(new[] { "a", "b c" }, (IEnumerable<string>)Read(typeof(IEnumerable<string>), "[a, \"b c\"]", out _));
            Assert.AreEqual(0, ((int[])Read(typeof(int[]), "empty", out _)).Length);
            Assert.AreEqual("[1]: not a whole number: x", Problem(typeof(int[]), "[1, x]"));

            Assert.IsNull(Read(typeof(int?), "null", out _));
            Assert.AreEqual(4, Read(typeof(int?), "4", out _));
            Assert.AreEqual(typeof(Vector3), Read(typeof(Type), "UnityEngine.Vector3", out _));
            Assert.AreEqual(new Guid("0123456789abcdef0123456789abcdef"), Read(typeof(Guid), "0123456789abcdef0123456789abcdef", out int parsed));
            Assert.AreEqual(Values.Loose, parsed);
            StringAssert.StartsWith("not a Version (", Problem(typeof(Version), "one.two"));
        }

        [Test]
        public void Other_classes_take_name_value_pairs()
        {
            var shape = (RunShape)Read(typeof(RunShape), "{width: 3, height: 4}", out _);
            Assert.AreEqual(12, shape.Area);
            Assert.AreEqual("RunShape has no settable depth", Problem(typeof(RunShape), "depth: 2"));
            Assert.AreEqual("RunShape has no settable Area", Problem(typeof(RunShape), "Area: 2"));
            Assert.AreEqual("width: not a whole number: wide", Problem(typeof(RunShape), "{width: wide}"));
            Assert.AreEqual("ug run cannot read a Action from text (ug eval can build one)", Problem(typeof(Action), "x"));
        }
    }

    public class ValuesShowTests
    {
        private static string Show(object value) => Values.Show(value, Values.Caps.Compact, out _);

        [Test]
        public void Simple_values_print_as_ug_inspect_prints_them()
        {
            Assert.AreEqual("null", Show(null));
            Assert.AreEqual("\"a\\nb\"", Show("a\nb"));
            Assert.AreEqual("\"x\"", Show('x'));
            Assert.AreEqual("true", Show(true));
            Assert.AreEqual("0.1", Show(0.1f));
            Assert.AreEqual("42", Show(42L));
            Assert.AreEqual("Red, Blue", Show(RunFlags.Red | RunFlags.Blue));
            Assert.AreEqual("1, 2, 3", Show(new Vector3(1, 2, 3)));
            Assert.AreEqual("#ff8000ff", Show(new Color32(255, 128, 0, 255)));
            Assert.AreEqual("UnityEngine.Vector3", Show(typeof(Vector3)));
            Assert.AreEqual("(1, \"a\")", Show((1, "a")));
            Assert.AreEqual("10/10/2026 00:00:00", Show(new DateTime(2026, 10, 10)));
        }

        [Test]
        public void Collections_and_objects_are_capped()
        {
            bool cut;
            Assert.AreEqual("int[3] [1, 2, 3]", Values.Show(new[] { 1, 2, 3 }, Values.Caps.Compact, out cut));
            Assert.IsFalse(cut);
            Assert.AreEqual("int[25] [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, ... +15 more]", Values.Show(Enumerable.Range(1, 25).ToArray(), Values.Caps.Compact, out cut));
            Assert.IsTrue(cut);
            Assert.AreEqual("List<string>[0] []", Show(new List<string>()));
            Assert.AreEqual("Dictionary<string, int>[1] {\"a\": 1}", Show(new Dictionary<string, int> { { "a", 1 } }));
            Assert.AreEqual("RunShape {width: 2, Height: 3, Area: 6}", Show(new RunShape { width = 2, Height = 3 }));
            Assert.AreEqual("List<int[]>[1] [int[2]]", Values.Show(new List<int[]> { new[] { 1, 2 } }, Values.Caps.Compact, out cut));
            Assert.IsTrue(cut);
            Assert.AreEqual("List<int[]>[1] [int[2] [1, 2]]", Values.Show(new List<int[]> { new[] { 1, 2 } }, Values.Caps.Full, out _));

            string longText = new string('x', 250);
            Assert.AreEqual("\"" + new string('x', 200) + "\"... +50 chars", Values.Show(longText, Values.Caps.Compact, out cut));
            Assert.IsTrue(cut);
        }

        [Test]
        public void Unity_objects_print_as_addresses()
        {
            var made = new GameObject("UgShowProbe");
            try
            {
                Assert.AreEqual("UgShowProbe", Show(made));
                Assert.AreEqual("UgShowProbe:Transform", Show(made.transform));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(made);
            }

            Assert.AreEqual("missing (destroyed GameObject)", Show(made));
        }
    }

    public class MethodRunnerTests : OpsFixture
    {
        private Func<System.Reflection.Assembly, bool> _isOwn;

        [SetUp]
        public void OwnTestsOnly()
        {
            _isOwn = MethodRunner.IsOwn;
            MethodRunner.IsOwn = assembly => assembly == typeof(RunTargets).Assembly;
            RunTargets.Console = new ConsoleBuffer();
        }

        [TearDown]
        public void RestoreOwn() => MethodRunner.IsOwn = _isOwn;

        // The answer's text, its times as N.
        private static string Text(Reply reply)
        {
            string text = reply.Lines != null ? string.Join("\n", reply.Lines) : "error: " + JsonUtility.FromJson<ErrorBody>(reply.Body).error;
            return Regex.Replace(text, @"\d+(\.\d)? ms", "N ms");
        }

        private static Func<Reply> Start(string target, params string[] args)
        {
            var request = new Request { Path = "/run" };
            request.Query["target"] = target;
            request.Query["n"] = args.Length.ToString();
            for (int i = 0; i < args.Length; i++)
            {
                request.Query["a" + i] = args[i];
            }

            return MethodRunner.Run(request, RunTargets.Console);
        }

        private static string Run(string target, params string[] args)
        {
            Reply reply = Start(target, args)();
            Assert.IsNotNull(reply, "a synchronous call answers at once");
            return Text(reply);
        }

        private static string List(string filter, bool menu = false)
        {
            var request = new Request { Path = "/run" };
            request.Query["list"] = filter;
            if (menu)
            {
                request.Query["menu"] = "true";
            }

            return Text(MethodRunner.Run(request, null)());
        }

        [Serializable]
        private class ErrorBody
        {
            public string error;
        }

        [Test]
        public void Own_assemblies_are_script_assemblies_and_plugins_not_registry_package_dlls()
        {
            const string root = "D:\\Game";
            Assert.IsTrue(MethodRunner.OwnLocation("D:\\Game\\Library\\ScriptAssemblies\\Assembly-CSharp.dll", root));
            Assert.IsTrue(MethodRunner.OwnLocation("D:/Game/Assets/Plugins/Odin/Sirenix.Utilities.dll", root));
            Assert.IsTrue(MethodRunner.OwnLocation("D:/Game/Packages/com.me.tools/Plugins/Tool.dll", root));
            Assert.IsFalse(MethodRunner.OwnLocation("D:/Game/Library/PackageCache/com.unity.collab-proxy@2.9.3/Lib/Editor/unityplastic.dll", root));
            Assert.IsFalse(MethodRunner.OwnLocation("D:/Other/Library/ScriptAssemblies/Assembly-CSharp.dll", root));
        }

        [Test]
        public void Runs_by_full_or_short_name_with_any_access()
        {
            int calls = RunTargets.Calls;
            Assert.AreEqual("ran UnityGraph.Bridge.Tests.RunTargets.Hello() in N ms\n  result: \"hello\"", Run("UnityGraph.Bridge.Tests.RunTargets.Hello"));
            Assert.AreEqual("ran UnityGraph.Bridge.Tests.RunTargets.Hidden() in N ms\n  result: \"hidden\"", Run("RunTargets.Hidden"));
            Assert.AreEqual("ran UnityGraph.Bridge.Tests.RunTargets.Touch() in N ms", Run("RunTargets.Touch"));
            Assert.AreEqual(calls + 1, RunTargets.Calls);
        }

        [Test]
        public void Arguments_bind_by_position_name_default_and_params()
        {
            Assert.AreEqual("  result: 13", Last(Run("RunTargets.Sum", "3")));
            Assert.AreEqual("  result: 5", Last(Run("RunTargets.Sum", "3", "b=2")));
            Assert.AreEqual("  result: 7", Last(Run("RunTargets.Sum", "b=4", "a=3")));
            Assert.AreEqual("  result: 3", Last(Run("RunTargets.Count", "x", "1", "2", "3")));
            Assert.AreEqual("  result: 2", Last(Run("RunTargets.Count", "x", "[1, 2]")));
            Assert.AreEqual("  result: 0", Last(Run("RunTargets.Count", "x")));
            Assert.AreEqual("  result: 6", Last(Run("RunTargets.Total", "1, 2, 3")));
            Assert.AreEqual("  result: \"b=2\"", Last(Run("RunTargets.Echo", "b=2")));
            Assert.AreEqual("  result: \"text=2\"", Last(Run("RunTargets.Echo", "\\text=2")));
            Assert.AreEqual("  result: 12", Last(Run("RunTargets.Area", "{width: 3, height: 4}")));
            Assert.AreEqual("  result: \"(1.00, 2.00, 3.00) RGBA(1.000, 0.000, 0.000, 1.000) Fast Red, Blue\"", Last(Run("RunTargets.Place", "1,2,3", "#ff0000", "fast", "red|blue")));
            Assert.AreEqual("ran UnityGraph.Bridge.Tests.RunTargets.Split(string text, out string head, ref int count) in N ms\n  result: true\n  out head: \"a\"\n  out count: 3", Run("RunTargets.Split", "abc", "2"));
        }

        [Test]
        public void The_overload_whose_arguments_fit_best_runs()
        {
            Assert.AreEqual("  result: \"int\"", Last(Run("RunTargets.Pick", "3")));
            Assert.AreEqual("  result: \"float\"", Last(Run("RunTargets.Pick", "2.5")));
            Assert.AreEqual("  result: \"string\"", Last(Run("RunTargets.Pick", "three")));
            Assert.AreEqual(
                "error: did not run RunTargets.Tie: ambiguous call: these overloads fit equally; name the arguments (param=value) or write them more exactly\n  static string Tie(int value)\n  static string Tie(long value)",
                Run("RunTargets.Tie", "3"));
        }

        [Test]
        public void Refusals_say_why_and_what_to_use()
        {
            Assert.AreEqual("error: did not run RunTargets.Sum: a: not a whole number: many\n  static int Sum(int a, int b = 10)", Run("RunTargets.Sum", "many"));
            Assert.AreEqual("error: did not run RunTargets.Sum: takes 2 arguments, given 3\n  static int Sum(int a, int b = 10)", Run("RunTargets.Sum", "1", "2", "3"));
            Assert.AreEqual("error: did not run RunTargets.Sum: missing a (int)\n  static int Sum(int a, int b = 10)", Run("RunTargets.Sum"));
            Assert.AreEqual("error: did not run RunTargets.Sum: a positional argument after a named one: 2\n  static int Sum(int a, int b = 10)", Run("RunTargets.Sum", "b=1", "2"));
            StringAssert.StartsWith("error: did not run RunTargets.Pick: no overload takes these arguments\n  static string Pick(", Run("RunTargets.Pick", "1", "2"));
            Assert.AreEqual("error: did not run RunTargets.Mine: an instance method: ug eval calls it on an object", Run("RunTargets.Mine"));
            Assert.AreEqual("error: did not run RunTargets.Generic: a generic method: ug eval calls it with its type arguments", Run("RunTargets.Generic", "1"));
            StringAssert.StartsWith("error: did not run RunTargets.Nope: no static method Nope on UnityGraph.Bridge.Tests.RunTargets (static methods: ", Run("RunTargets.Nope"));
            Assert.AreEqual("error: did not run Nowhere.Run: no type Nowhere in the project's assemblies (ug run --list lists them)", Run("Nowhere.Run"));
            Assert.AreEqual(
                "error: did not run UnityEditor.AssetDatabase.Refresh: UnityEditor.AssetDatabase is Unity's, not the project's: ug eval \"UnityEditor.AssetDatabase.Refresh()\" calls it",
                Run("UnityEditor.AssetDatabase.Refresh"));
            Assert.AreEqual("error: did not run Hello: target must be Type.Method or a menu path", Run("Hello"));
            StringAssert.StartsWith("error: give a target: ", Run(" "));
            Assert.AreEqual("error: did not run File/Quit: refused, it would close the editor (ug does not quit Unity)", Run("File/Quit"));
            Assert.AreEqual("error: did not run Tools/Some Item: a menu item takes no arguments", Run("Tools/Some Item", "1"));
            LogAssert.ignoreFailingMessages = true; // Unity logs its own error for a missing menu item
            Assert.AreEqual("error: did not run Nowhere/At All: no menu item Nowhere/At All (ug run --list --menu lists them)", Run("Nowhere/At All"));
        }

        [Test]
        public void An_exception_is_a_failed_answer_with_its_first_line()
        {
            string answer = Run("RunTargets.Boom");
            StringAssert.StartsWith("error: UnityGraph.Bridge.Tests.RunTargets.Boom() threw after N ms: InvalidOperationException: boom", answer);
            StringAssert.DoesNotContain("second line", answer);
        }

        [Test]
        public void Logs_and_modified_scenes_follow_the_result()
        {
            // The entry before the call repeats in it: only the call's repeats count.
            RunTargets.Console.Add("log", "first", "Assets/Tools.cs:3");
            Assert.AreEqual("ran UnityGraph.Bridge.Tests.RunTargets.Log() in N ms\n  log: first x2  Assets/Tools.cs:3\n  warning: second", Run("RunTargets.Log"));

            Assert.AreEqual(
                "ran UnityGraph.Bridge.Tests.RunTargets.Spawn(string name) in N ms\n  result: Crate\n  changed: Main.unity modified, not saved",
                Run("RunTargets.Spawn", "Crate"));
            Assert.AreEqual("undid spawn", Text(EditOps.UndoLast(false)));
            Assert.IsNull(GameObject.Find("Crate"));
        }

        [Test]
        public void A_cut_result_is_written_whole_to_the_result_file()
        {
            Assert.AreEqual("  result: int[25] [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, ... +15 more] (+more: Library/unitygraph/result.txt)", Last(Run("RunTargets.Many")));
            string written = System.IO.File.ReadAllText(System.IO.Path.Combine(Bridge.ProjectRoot, CodeRun.ResultFile));
            Assert.AreEqual("result: int[25] [" + string.Join(", ", Enumerable.Range(1, 25)) + "]\n", written);
        }

        // Polls on editor updates, ticking the waits as Bridge.Update does, for at most five seconds.
        private static IEnumerator Await(Func<Reply> poll, Reply[] answer)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while ((answer[0] = poll()) == null && clock.Elapsed.TotalSeconds < 5)
            {
                CodeRun.Tick();
                yield return null;
            }
        }

        [Test]
        public void An_enumerator_is_stepped_once_per_tick_with_nested_ones_inside()
        {
            Func<Reply> poll = Start("RunTargets.Steps");
            var polls = new List<string>();
            for (int tick = 0; tick < 10; tick++)
            {
                Reply reply = poll();
                polls.Add(reply == null ? "waiting" : Text(reply));
                if (reply != null)
                {
                    break;
                }

                CodeRun.Tick();
            }

            Assert.AreEqual("waiting|waiting|waiting|waiting|ran UnityGraph.Bridge.Tests.RunTargets.Steps() in N ms\n  result: finished after 3 steps", string.Join("|", polls));
        }

        [UnityTest]
        public IEnumerator A_task_is_awaited_across_editor_updates()
        {
            var answer = new Reply[1];
            yield return Await(Start("RunTargets.Later"), answer);
            Assert.IsNotNull(answer[0], "no answer in 5 s");
            Assert.AreEqual("ran UnityGraph.Bridge.Tests.RunTargets.Later() in N ms\n  result: 7", Text(answer[0]));
        }

        [UnityTest]
        public IEnumerator Past_the_timeout_the_answer_says_it_goes_on()
        {
            var request = new Request { Path = "/run" };
            request.Query["target"] = "RunTargets.Forever";
            request.Query["timeout"] = "0.2";
            RunTargets.Pending = new TaskCompletionSource<bool>();
            var answer = new Reply[1];
            yield return Await(MethodRunner.Run(request, RunTargets.Console), answer);
            Assert.IsNotNull(answer[0], "no answer in 5 s");
            Assert.AreEqual("UnityGraph.Bridge.Tests.RunTargets.Forever() still running after 0.2 s; it goes on in the editor", Text(answer[0]));
            RunTargets.Pending.SetResult(true);
            CodeRun.Tick();
        }

        [Test]
        public void Lists_types_methods_and_menu_items()
        {
            StringAssert.Contains("UnityGraph.Bridge.Tests.RunTargets  ", List(""));
            string methods = List("RunTargets.Sum");
            StringAssert.StartsWith("UnityGraph.Bridge.Tests.RunTargets  1 static method", methods);
            StringAssert.Contains("  static int Sum(int a, int b = 10)", methods);
            Assert.AreEqual("no static method in the project's assemblies matches Nowhere (ug find searches the graph)", List("Nowhere"));
            StringAssert.DoesNotContain("File/Quit", List("File/", menu: true));
        }

        [Test]
        public void Framework_assemblies_are_never_own()
        {
            Assert.IsTrue(MethodRunner.IsFramework("UnityEditor.CoreModule"));
            Assert.IsTrue(MethodRunner.IsFramework("System.Core"));
            Assert.IsTrue(MethodRunner.IsFramework("mscorlib"));
            Assert.IsTrue(MethodRunner.IsFramework("Unity.TextMeshPro"));
            Assert.IsTrue(MethodRunner.IsFramework("UnityGraph.Bridge.Editor"));
            Assert.IsFalse(MethodRunner.IsFramework("Assembly-CSharp-Editor"));
            Assert.IsFalse(MethodRunner.IsFramework("Game.Lab"));
        }

        private static string Last(string text) => text.Split('\n').Last();
    }
}
