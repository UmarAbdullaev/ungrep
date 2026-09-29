using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityGraph.Bridge.Tests
{
    public static class RunTargets
    {
        public static int Calls;

        public static string Hello() => "hello";

        public static void Touch() => Calls++;

        public static void Boom() => throw new InvalidOperationException("boom");

        public static string Echo(string text) => text;

        private static string Hidden() => "hidden";
    }

    public class JsonTests
    {
        [Test]
        public void Writes_nested_values_with_commas_and_escapes()
        {
            string json = new Json().Object()
                .Field("a", 1L)
                .Field("b", "x\"y\\z\n\t\u0001")
                .Field("c", (string)null)
                .Field("d", true)
                .Field("e", 1.25)
                .Key("f").Array().Value(1L).Object().Field("g", false).EndObject().Array().EndArray().EndArray()
                .Key("h").Raw("{\"i\":2}")
                .EndObject().ToString();

            Assert.AreEqual("{\"a\":1,\"b\":\"x\\\"y\\\\z\\n\\t\\u0001\",\"c\":null,\"d\":true,\"e\":1.25,\"f\":[1,{\"g\":false},[]],\"h\":{\"i\":2}}", json);
        }
    }

    public class StackFramesTests
    {
        private static string Root => Path.GetDirectoryName(Application.dataPath);

        [Test]
        public void Finds_the_first_own_frame_in_a_log_trace()
        {
            const string trace = "UnityEngine.Debug:LogError (object)\nGame.Lab.Turntable:Stop () (at Assets/Scripts/Lab/Turntable.cs:23)\nGame.Lab.LabStage:OnClosed () (at Assets/Scripts/Lab/LabStage.cs:51)\n";

            Assert.AreEqual("Assets/Scripts/Lab/Turntable.cs:23", StackFrames.OwnFrame(trace, Root));
        }

        [Test]
        public void Finds_an_absolute_frame_inside_the_project_in_an_exception_trace()
        {
            string trace = "  at Some.Package.Thing () [0x00001] in C:\\elsewhere\\Thing.cs:4 \n  at Game.Foo.Bar () [0x00010] in " + Root.Replace('/', '\\') + "\\Assets\\Scripts\\Foo.cs:12 \n";

            Assert.AreEqual("Assets/Scripts/Foo.cs:12", StackFrames.OwnFrame(trace, Root));
        }

        [Test]
        public void Package_cache_and_missing_traces_have_no_own_frame()
        {
            Assert.IsNull(StackFrames.OwnFrame("X:Y () (at ./Library/PackageCache/com.unity.ugui/Runtime/Button.cs:70)", Root));
            Assert.IsNull(StackFrames.OwnFrame("X:Y () (at Packages/com.not.embedded/Runtime/A.cs:3)", Root));
            Assert.IsNull(StackFrames.OwnFrame("  at System.IO.Path.IsPathRooted (System.String path) [0x0001b] in <41d29b352f6a475ab1bf7c6628b82790>:0 ", Root));
            Assert.IsNull(StackFrames.OwnFrame("", Root));
            Assert.IsNull(StackFrames.OwnFrame(null, Root));
        }
    }

    public class ConsoleBufferTests
    {
        [Test]
        public void A_repeat_bumps_the_count_and_the_id()
        {
            var buffer = new ConsoleBuffer();
            buffer.Add("log", "a", null);
            buffer.Add("error", "b", "Assets/B.cs:1");
            buffer.Add("error", "b", "Assets/B.cs:1");

            List<ConsoleBuffer.Entry> all = buffer.Query(0, 50, null);

            Assert.AreEqual(2, all.Count);
            Assert.AreEqual(3, all[1].Id);
            Assert.AreEqual(2, all[1].Count);
            Assert.AreEqual(3, buffer.LastId);
            Assert.AreEqual("b", buffer.Query(2, 50, null).Single().Text);
        }

        [Test]
        public void Query_filters_by_type_and_keeps_the_newest_within_the_limit()
        {
            var buffer = new ConsoleBuffer();
            for (int i = 0; i < 10; i++)
            {
                buffer.Add(i % 2 == 0 ? "log" : "warning", "m" + i, null);
            }

            List<ConsoleBuffer.Entry> warnings = buffer.Query(0, 2, new HashSet<string> { "warning" });

            CollectionAssert.AreEqual(new[] { "m7", "m9" }, warnings.Select(e => e.Text).ToArray());
        }

        [Test]
        public void Old_entries_fall_off_at_capacity()
        {
            var buffer = new ConsoleBuffer();
            for (int i = 0; i < ConsoleBuffer.Capacity + 5; i++)
            {
                buffer.Add("log", "m" + i, null);
            }

            List<ConsoleBuffer.Entry> all = buffer.Query(0, 10000, null);

            Assert.AreEqual(ConsoleBuffer.Capacity, all.Count);
            Assert.AreEqual("m5", all[0].Text);
        }

        [Test]
        public void Save_and_restore_keep_every_field()
        {
            var buffer = new ConsoleBuffer();
            buffer.Add("error", "tab\there\nnew line \\ slash", "Assets/A.cs:3");
            buffer.Add("log", "plain", null);

            var restored = new ConsoleBuffer();
            restored.Restore(buffer.Save());
            restored.Add("log", "after", null);

            List<ConsoleBuffer.Entry> all = restored.Query(0, 50, null);
            Assert.AreEqual(3, all.Count);
            Assert.AreEqual("tab\there\nnew line \\ slash", all[0].Text);
            Assert.AreEqual("Assets/A.cs:3", all[0].Frame);
            Assert.IsNull(all[1].Frame);
            Assert.AreEqual(3, all[2].Id);
        }

        [Test]
        public void Unreadable_saved_text_leaves_the_buffer_empty()
        {
            var buffer = new ConsoleBuffer();
            buffer.Restore("not a number\n");

            Assert.AreEqual(0, buffer.Query(0, 50, null).Count);
        }
    }

    public class CompileLogTests
    {
        [Test]
        public void Messages_get_code_text_and_a_relative_path()
        {
            string root = Path.GetDirectoryName(Application.dataPath);
            CompileLog.Message message = CompileLog.Convert("Assembly-CSharp", root + "/Assets/Scripts/Turntable.cs", 17, 9, true,
                "Assets/Scripts/Turntable.cs(17,9): error CS0103: The name 'spd' does not exist in the current context", root);

            Assert.AreEqual("Assets/Scripts/Turntable.cs", message.File);
            Assert.AreEqual("error", message.Severity);
            Assert.AreEqual("CS0103", message.Code);
            Assert.AreEqual("The name 'spd' does not exist in the current context", message.Text);
        }

        [Test]
        public void The_answer_lists_every_message()
        {
            var messages = new List<CompileLog.Message>
            {
                CompileLog.Convert("A", "Assets/A.cs", 1, 2, false, "Assets/A.cs(1,2): warning CS0168: unused", "/p"),
            };

            string json = CompileLog.Write(3, new DateTime(2026, 9, 29, 14, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 29, 14, 0, 4, DateTimeKind.Utc), messages);

            StringAssert.StartsWith("{\"compile\":3,\"started\":\"2026-09-29T14:00:00.0000000Z\",", json);
            StringAssert.Contains("{\"assembly\":\"A\",\"file\":\"Assets/A.cs\",\"line\":1,\"column\":2,\"severity\":\"warning\",\"code\":\"CS0168\",\"text\":\"unused\"}", json);
        }
    }

    public class MethodRunnerTests
    {
        private static bool TestsOnly(string assembly) => assembly == typeof(RunTargets).Assembly.GetName().Name;

        [Test]
        public void Runs_by_full_or_short_type_name()
        {
            MethodRunner.Outcome full = MethodRunner.Run("UnityGraph.Bridge.Tests.RunTargets.Hello", TestsOnly, "/p");
            MethodRunner.Outcome shortName = MethodRunner.Run("RunTargets.Hidden", TestsOnly, "/p");
            int calls = RunTargets.Calls;
            MethodRunner.Outcome touched = MethodRunner.Run("RunTargets.Touch", TestsOnly, "/p");

            Assert.IsTrue(full.Ran);
            Assert.AreEqual("hello", full.Result);
            Assert.AreEqual("hidden", shortName.Result);
            Assert.IsTrue(touched.Ran);
            Assert.IsNull(touched.Result);
            Assert.AreEqual(calls + 1, RunTargets.Calls);
        }

        [Test]
        public void An_exception_is_the_answer_not_a_failure()
        {
            MethodRunner.Outcome outcome = MethodRunner.Run("RunTargets.Boom", TestsOnly, "/p");

            Assert.IsTrue(outcome.Ran);
            Assert.AreEqual("System.InvalidOperationException: boom", outcome.Error);
        }

        [Test]
        public void Refuses_what_it_cannot_run()
        {
            Assert.AreEqual("no static parameterless method Echo on UnityGraph.Bridge.Tests.RunTargets", MethodRunner.Run("RunTargets.Echo", TestsOnly, "/p").Error);
            Assert.AreEqual("no type Nowhere in the project's assemblies", MethodRunner.Run("Nowhere.Run", TestsOnly, "/p").Error);
            Assert.AreEqual("no type UnityEditor.EditorApplication in the project's assemblies", MethodRunner.Run("UnityEditor.EditorApplication.Exit", TestsOnly, "/p").Error);
            Assert.AreEqual("target must be Type.Method or a menu path: Hello", MethodRunner.Run("Hello", TestsOnly, "/p").Error);
            Assert.AreEqual("no target", MethodRunner.Run(" ", TestsOnly, "/p").Error);
            LogAssert.ignoreFailingMessages = true; // Unity logs its own error for a missing menu item
            Assert.AreEqual("no menu item Nowhere/At All", MethodRunner.Run("Nowhere/At All", TestsOnly, "/p").Error);
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
    }

    public class ServerTests
    {
        private const string Token = "0123456789abcdef0123456789abcdef";

        [Test]
        public void Access_needs_the_token_and_no_origin()
        {
            Assert.IsNull(Server.Check(Token, Token, null));
            Assert.AreEqual(401, Server.Check(Token, "wrong", null).Status);
            Assert.AreEqual(401, Server.Check(Token, null, null).Status);
            Assert.AreEqual(403, Server.Check(Token, Token, "http://evil.example").Status);
        }

        [Test]
        public void Query_parameters_are_decoded()
        {
            Request request = Request.From(new Uri("http://127.0.0.1:1/run?target=Tools%2FBuild+Lab&x"));

            Assert.AreEqual("/run", request.Path);
            Assert.AreEqual("Tools/Build Lab", request.Get("target"));
            Assert.AreEqual("", request.Get("x"));
            Assert.IsNull(request.Get("y"));
        }

        [Test]
        public void Answers_status_on_its_thread_and_the_rest_through_the_pump()
        {
            var server = new Server(Token, () => "{\"state\":\"idle\"}");
            server.Start(0);
            try
            {
                Assert.AreEqual("200 {\"state\":\"idle\"}", Get(server.Port, "/status", Token));
                Assert.AreEqual("401 {\"error\":\"missing or wrong X-UG-Token\"}", Get(server.Port, "/status", "nope"));

                string answer = null;
                var thread = new Thread(() => answer = Get(server.Port, "/echo?text=hi", Token));
                thread.Start();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (thread.IsAlive && DateTime.UtcNow < deadline)
                {
                    // The second request waits one extra pump, like a refresh does.
                    server.Pump(r => { int seen = 0; return () => ++seen < 2 ? null : Reply.Ok("{\"echo\":\"" + r.Get("text") + "\"}"); });
                    Thread.Sleep(5);
                }

                Assert.AreEqual("200 {\"echo\":\"hi\"}", answer);
            }
            finally
            {
                server.Stop();
            }
        }

        [Test]
        public void A_taken_preferred_port_falls_back_to_a_free_one()
        {
            var first = new Server(Token, () => "{}");
            var second = new Server(Token, () => "{}");
            first.Start(0);
            try
            {
                second.Start(first.Port);
                Assert.AreNotEqual(first.Port, second.Port);
            }
            finally
            {
                second.Stop();
                first.Stop();
            }
        }

        private static string Get(int port, string path, string token)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            request.Headers["X-UG-Token"] = token;
            request.Timeout = 10000;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    return (int)response.StatusCode + " " + reader.ReadToEnd();
                }
            }
            catch (WebException e) when (e.Response is HttpWebResponse failed)
            {
                using (failed)
                using (var reader = new StreamReader(failed.GetResponseStream()))
                {
                    return (int)failed.StatusCode + " " + reader.ReadToEnd();
                }
            }
        }
    }
}
