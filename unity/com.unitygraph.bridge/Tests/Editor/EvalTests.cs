using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge.Tests
{
    public class EvalTargets
    {
        private static class Inner
        {
            private static string Go(string name = "inner") => name;
        }
    }

    /// <summary>`/eval/refs`, `/eval` and the loop guard (SPEC-editor-link, Running code).</summary>
    public class EvalTests
    {
        private static readonly string Scratch = Path.Combine(Bridge.ProjectRoot, "Temp", "ug-eval-tests");
        private static byte[] _snippet;

        // An assembly with its PDB, built by the editor's own csc against what /eval/refs lists, as ug builds one.
        [OneTimeSetUp]
        public void BuildSnippet()
        {
            string contents = EditorApplication.applicationContentsPath;
            string dotnet = Path.Combine(contents, "NetCoreRuntime", Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet");
            string csc = Path.Combine(contents, "DotNetSdkRoslyn", "csc.dll");
            if (!File.Exists(dotnet) || !File.Exists(csc))
            {
                return;
            }

            Directory.CreateDirectory(Scratch);
            string name = EvalRunner.AssemblyPrefix + Guid.NewGuid().ToString("N");
            string source = Path.Combine(Scratch, "snippet.cs");
            File.WriteAllText(
                source,
                "#line 1 \"snippet\"\n"
                + "public static class __UgSnippet { public static object Run() {\n"
                + "int total = 0; for (int i = 0; i < 3; i++) { global::UnityGraph.Bridge.EvalGuard.Check(60D); total += i; }\n"
                + "throw new System.InvalidOperationException(\"from the snippet \" + total);\n"
                + "} private static int Twice(int x, int times = 2) => x * times; }\n");
            var options = new List<string> { "-nologo", "-noconfig", "-nostdlib", "-target:library", "-debug:portable", "-out:\"" + Path.Combine(Scratch, name + ".dll") + "\"" };
            options.AddRange(Refs().refs.Select(r => "-r:\"" + r + "\""));
            options.Add("\"" + source + "\"");
            File.WriteAllLines(Path.Combine(Scratch, "csc.rsp"), options);

            var start = new ProcessStartInfo(dotnet, "\"" + csc + "\" @\"" + Path.Combine(Scratch, "csc.rsp") + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd();
                Assert.IsTrue(process.WaitForExit(120000), "csc did not finish");
                Assert.AreEqual(0, process.ExitCode, output);
            }

            byte[] dll = File.ReadAllBytes(Path.Combine(Scratch, name + ".dll"));
            byte[] pdb = File.ReadAllBytes(Path.Combine(Scratch, name + ".pdb"));
            _snippet = Body(dll, pdb);
        }

        [OneTimeTearDown]
        public void CleanUp()
        {
            EvalGuard.Beat();
            try
            {
                Directory.Delete(Scratch, true);
            }
            catch (IOException)
            {
            }
        }

        [Serializable]
        private class RefsBody
        {
            public string[] refs;
            public string[] defines;
            public string lang;
        }

        [Serializable]
        private class ErrorBody
        {
            public string error;
        }

        private static RefsBody Refs() => JsonUtility.FromJson<RefsBody>(EvalRunner.Refs().Body);

        private static byte[] Body(byte[] image, byte[] symbols)
        {
            var body = new byte[4 + image.Length + symbols.Length];
            BitConverter.GetBytes(image.Length).CopyTo(body, 0);
            image.CopyTo(body, 4);
            symbols.CopyTo(body, 4 + image.Length);
            return body;
        }

        // The answer's text, its times as N.
        private static string Text(Reply reply)
        {
            string text = reply.Lines != null ? string.Join("\n", reply.Lines) : "error: " + JsonUtility.FromJson<ErrorBody>(reply.Body).error;
            return Regex.Replace(text, @"\d+(\.\d)? ms", "N ms");
        }

        private static string Eval(byte[] body, string entry, string what = null, string source = "snippet", params string[] args)
        {
            var request = new Request { Path = "/eval", Body = body };
            request.Query["source"] = source;
            request.Query["entry"] = entry;
            request.Query["n"] = args.Length.ToString();
            for (int i = 0; i < args.Length; i++)
            {
                request.Query["a" + i] = args[i];
            }

            if (what != null)
            {
                request.Query["what"] = what;
            }

            Reply reply = EvalRunner.Run(request, new ConsoleBuffer())();
            Assert.IsNotNull(reply, "a synchronous call answers at once");
            return Text(reply);
        }

        private static string Bound(string entry, params string[] args)
        {
            string problem = EvalRunner.Prepare(typeof(EvalTests).Assembly, "Tools/Lab.cs", null, entry, args.ToList(), 60, out CodeRun.Call call);
            return problem ?? call.What + " in " + call.Snippet;
        }

        [Test]
        public void Only_eval_takes_a_post_and_its_body_is_capped()
        {
            Assert.IsNull(Server.Method("GET", "/run"));
            Assert.IsNull(Server.Method("POST", "/eval"));
            Assert.AreEqual(405, Server.Method("POST", "/run").Status);
            Assert.AreEqual(405, Server.Method("PUT", "/eval").Status);

            var bytes = new byte[100];
            Assert.AreEqual(100, Server.ReadBody(new MemoryStream(bytes), 100, 100).Length);
            Assert.AreEqual(100, Server.ReadBody(new MemoryStream(bytes), -1, 100).Length);
            Assert.IsNull(Server.ReadBody(new MemoryStream(bytes), -1, 99), "a chunked body over the cap");
            Assert.IsNull(Server.ReadBody(new MemoryStream(bytes), 200, 100), "a declared length over the cap");
        }

        [Test]
        public void The_body_is_the_assembly_length_then_assembly_then_pdb()
        {
            Assert.IsTrue(EvalRunner.Split(Body(new byte[] { 1, 2, 3 }, new byte[] { 9 }), out byte[] image, out byte[] symbols));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, image);
            CollectionAssert.AreEqual(new byte[] { 9 }, symbols);
            Assert.IsFalse(EvalRunner.Split(new byte[] { 9, 0, 0, 0, 1 }, out _, out _), "a length past the end");
            Assert.IsFalse(EvalRunner.Split(new byte[] { 1 }, out _, out _));
            Assert.AreEqual("error: did not run snippet: no assembly in the request (ug eval sends one)", Eval(null, "__UgSnippet.Run"));
            StringAssert.StartsWith("error: did not run snippet: not an assembly: ", Eval(Body(new byte[] { 1, 2, 3 }, new byte[0]), "__UgSnippet.Run"));
        }

        [Test]
        public void Refs_are_the_loaded_assemblies_files_and_the_runtime_facades()
        {
            RefsBody refs = Refs();
            Assert.That(refs.refs, Has.Some.EndsWith("/mscorlib.dll"));
            Assert.That(refs.refs, Has.Some.EndsWith("/UnityEditor.CoreModule.dll"));
            Assert.That(refs.refs, Has.Some.EndsWith("/UnityGraph.Bridge.Editor.dll"));
            Assert.That(refs.refs, Has.Some.EndsWith("Facades/netstandard.dll"));
            Assert.That(refs.refs, Has.None.Contains(EvalRunner.AssemblyPrefix));
            Assert.AreEqual(refs.refs.Length, refs.refs.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "one file per name");
            CollectionAssert.Contains(refs.defines, "UNITY_EDITOR");
            Assert.AreEqual("9.0", refs.lang);
            Assert.AreSame(EvalRunner.Refs(), EvalRunner.Refs(), "listed once until more assemblies load");
        }

        [Test]
        public void An_entry_binds_its_arguments_as_ug_run_binds_them()
        {
            Assert.AreEqual("UnityGraph.Bridge.Tests.RunTargets.Sum(int a, int b) in Tools/Lab.cs", Bound("UnityGraph.Bridge.Tests.RunTargets.Sum", "3"));
            Assert.AreEqual("UnityGraph.Bridge.Tests.EvalTargets.Inner.Go(string name) in Tools/Lab.cs", Bound("UnityGraph.Bridge.Tests.EvalTargets+Inner.Go"));
            Assert.AreEqual("a: not a whole number: many\n  static int Sum(int a, int b = 10)", Bound("UnityGraph.Bridge.Tests.RunTargets.Sum", "many"));
            Assert.AreEqual("entry must be Type.Method", Bound("Run"));
            Assert.AreEqual("no type Nowhere in the compiled code", Bound("Nowhere.Run"));
            Assert.AreEqual("no static method Nope on UnityGraph.Bridge.Tests.EvalTargets.Inner", Bound("UnityGraph.Bridge.Tests.EvalTargets+Inner.Nope"));
        }

        [Test]
        public void A_loop_guard_throws_once_the_editor_loop_is_away_too_long()
        {
            EvalGuard.Beat(Stopwatch.GetTimestamp() - (10 * Stopwatch.Frequency));
            for (int i = 0; i < 4096; i++)
            {
                EvalGuard.Check(60);
            }

            for (int i = 1; i < 1024; i++)
            {
                EvalGuard.Check(5);
            }

            TimeoutException stopped = Assert.Throws<TimeoutException>(() => EvalGuard.Check(5), "checked on the 1,024th call");
            Assert.AreEqual("a loop kept the editor waiting for 5 s; ug stopped it", stopped.Message);

            EvalGuard.Beat();
            for (int i = 0; i < 4096; i++)
            {
                EvalGuard.Check(5);
            }
        }

        [Test]
        public void A_snippet_runs_from_memory_and_its_frames_name_the_snippet()
        {
            if (_snippet == null)
            {
                Assert.Ignore("this editor has no DotNetSdkRoslyn/csc.dll to build a snippet with");
            }

            // csc records `#line "snippet"` against the source's folder; ug's compile keeps the name as written.
            string source = Path.Combine(Scratch, "snippet");
            Assert.AreEqual(
                "error: snippet threw after N ms: InvalidOperationException: from the snippet 3\n  at " + source + ":3\n  whole exception: Library/unitygraph/exception.txt",
                Eval(_snippet, "__UgSnippet.Run", "snippet", source));
            Assert.AreEqual("ran __UgSnippet.Twice(int x, int times) in N ms\n  result: 42", Eval(_snippet, "__UgSnippet.Twice", null, "Tools/Lab.cs", "21"));
            Assert.AreEqual("ran __UgSnippet.Twice(int x, int times) in N ms\n  result: 63", Eval(_snippet, "__UgSnippet.Twice", null, "Tools/Lab.cs", "21", "times=3"));
            Assert.AreEqual("error: did not run Tools/Lab.cs: no static method Thrice on __UgSnippet", Eval(_snippet, "__UgSnippet.Thrice", null, "Tools/Lab.cs"));

            // What the code logs later is located the same way: a name an eval ran under stays own.
            Assert.AreEqual("Tools/Lab.cs:5", StackFrames.OwnFrame("Lab:Go () (at Tools/Lab.cs:5)", Bridge.ProjectRoot));
        }
    }
}
