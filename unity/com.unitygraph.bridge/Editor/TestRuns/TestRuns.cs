using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Test runs through <see cref="TestRunnerApi"/>. The running id and each finished run's
    /// answer live in SessionState, so a PlayMode run's domain reloads do not lose them. Every
    /// failure is kept with its message and stack, each up to <see cref="ConsoleBuffer.MaxText"/>
    /// characters (SPEC-unity-loop, Nothing out of reach); an empty id reads the last run. A run is
    /// refused while a loaded scene is modified, and one the test runner cancels or stops on an
    /// error answers `stopped` (SPEC-unity-loop, Modified scenes and stopped runs).
    /// </summary>
    [InitializeOnLoad]
    internal sealed class TestRuns : ITestRuns, IErrorCallbacks
    {
        private const string RunningKey = "unitygraph.tests.running";
        private const string ResultKey = "unitygraph.tests.result.";
        private const string LastKey = "unitygraph.tests.last";

        // The test framework's internal TestRunnerApi.IsRunning(guid) (1.6.0); null on a framework without it.
        private static readonly Func<string, bool> _isRunning = IsRunningCheck();

        private readonly TestRunnerApi _api;

        static TestRuns()
        {
            Bridge.TestRuns = new TestRuns();
        }

        private TestRuns()
        {
            _api = ScriptableObject.CreateInstance<TestRunnerApi>();
            _api.RegisterCallbacks(this);
        }

        public string Start(string platform, string filter, out string error)
        {
            error = null;
            if (Running().Length > 0)
            {
                error = "a test run is in progress";
                return null;
            }

            TestMode mode;
            switch (platform)
            {
                case "EditMode": mode = TestMode.EditMode; break;
                case "PlayMode": mode = TestMode.PlayMode; break;
                default:
                    error = "platform must be EditMode or PlayMode";
                    return null;
            }

            // The run's first step asks to save modified scenes: a dialog batch mode never answers,
            // and Save would write the user's scene.
            List<string> modified = Addresses.LoadedScenes().Where(s => s.isDirty).Select(SceneOps.Name).ToList();
            if (modified.Count > 0)
            {
                error = "unsaved changes in " + string.Join(", ", modified) + " (the test runner would ask to save them): ug save or ug undo first";
                return null;
            }

            var runFilter = new Filter { testMode = mode };
            if (!string.IsNullOrEmpty(filter))
            {
                runFilter.groupNames = new[] { filter };
            }

            // Marked running before Execute: an EditMode run can finish inside the call.
            SessionState.SetString(RunningKey, "pending");
            string id = _api.Execute(new ExecutionSettings(runFilter));
            if (SessionState.GetString(RunningKey, "") == "pending")
            {
                SessionState.SetString(RunningKey, id);
            }
            else
            {
                MoveResult("pending", id);
                SessionState.SetString(LastKey, id);
            }

            return id;
        }

        public string Result(string id)
        {
            string running = Running();
            if (id.Length == 0)
            {
                id = SessionState.GetString(LastKey, "");
                if (id.Length == 0)
                {
                    return new Json().Object().Field("state", "none").EndObject().ToString();
                }
            }

            if (running.Length > 0 && running == id)
            {
                return new Json().Object().Field("state", "running").EndObject().ToString();
            }

            string answer = SessionState.GetString(ResultKey + id, "");
            return answer.Length > 0 ? answer : null;
        }

        public void RunStarted(ITestAdaptor testsToRun)
        {
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            string id = SessionState.GetString(RunningKey, "");
            if (id.Length == 0)
            {
                return; // a run someone started in the Test Runner window
            }

            var failed = new List<ITestResultAdaptor>();
            Collect(result, failed);
            var json = new Json().Object()
                .Field("state", "finished")
                .Field("passed", result.PassCount)
                .Field("failed", result.FailCount)
                .Field("skipped", result.SkipCount)
                .Field("inconclusive", result.InconclusiveCount)
                .Field("seconds", result.Duration)
                .Key("failures").Array();
            foreach (ITestResultAdaptor failure in failed)
            {
                json.Object()
                    .Field("name", failure.FullName)
                    .Field("message", failure.Message == null ? null : ConsoleBuffer.Bound(failure.Message))
                    .Field("frame", StackFrames.OwnFrame(failure.StackTrace, Bridge.ProjectRoot))
                    .Field("stack", string.IsNullOrEmpty(failure.StackTrace) ? null : ConsoleBuffer.Bound(failure.StackTrace.TrimEnd()))
                    .EndObject();
            }

            End(id, json.EndArray().EndObject().ToString());
        }

        /// <summary>The test runner stopped the run on an error (a failing IPrebuildSetup, a build): no RunFinished follows.</summary>
        public void OnError(string message)
        {
            string id = SessionState.GetString(RunningKey, "");
            if (id.Length > 0)
            {
                Stopped(id, "Unity stopped the run: " + ConsoleBuffer.Bound((message ?? "").Trim()) + " (ug console shows the error)");
            }
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
        }

        // The bridge's running id, or empty. A run the test runner no longer has ended without
        // RunFinished (canceled: its save-scenes dialog, the Test Runner window's Cancel) and is let
        // go here. A live run never reads as gone: Execute registers it, and after a domain reload
        // the test runner registers it again in an InitializeOnLoadMethod, before the editor update
        // where the bridge answers.
        private static string Running()
        {
            string id = SessionState.GetString(RunningKey, "");
            if (id.Length > 0 && _isRunning != null && !_isRunning(id))
            {
                Stopped(id, "canceled in Unity before it finished");
                return "";
            }

            return id;
        }

        private static void Stopped(string id, string reason) =>
            End(id, new Json().Object().Field("state", "stopped").Field("reason", reason).EndObject().ToString());

        private static void End(string id, string answer)
        {
            SessionState.SetString(ResultKey + id, answer);
            SessionState.SetString(LastKey, id);
            SessionState.EraseString(RunningKey);
        }

        private static Func<string, bool> IsRunningCheck()
        {
            MethodInfo method = typeof(TestRunnerApi).GetMethod("IsRunning", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            return method != null && method.ReturnType == typeof(bool)
                ? (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), method)
                : null;
        }

        private static void MoveResult(string from, string to)
        {
            string answer = SessionState.GetString(ResultKey + from, "");
            SessionState.EraseString(ResultKey + from);
            if (answer.Length > 0)
            {
                SessionState.SetString(ResultKey + to, answer);
            }
        }

        private static void Collect(ITestResultAdaptor result, List<ITestResultAdaptor> failed)
        {
            if (result.HasChildren)
            {
                foreach (ITestResultAdaptor child in result.Children)
                {
                    Collect(child, failed);
                }
            }
            else if (result.TestStatus == TestStatus.Failed)
            {
                failed.Add(result);
            }
        }
    }
}
