using System.Collections.Generic;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Test runs through <see cref="TestRunnerApi"/>. The running id and each finished run's
    /// answer live in SessionState, so a PlayMode run's domain reloads do not lose them.
    /// </summary>
    [InitializeOnLoad]
    internal sealed class TestRuns : ITestRuns, ICallbacks
    {
        private const string RunningKey = "unitygraph.tests.running";
        private const string ResultKey = "unitygraph.tests.result.";
        private const int MaxFailures = 50;

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
            if (SessionState.GetString(RunningKey, "").Length > 0)
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
            }

            return id;
        }

        public string Result(string id)
        {
            string running = SessionState.GetString(RunningKey, "");
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
            for (int i = 0; i < failed.Count && i < MaxFailures; i++)
            {
                json.Object()
                    .Field("name", failed[i].FullName)
                    .Field("message", failed[i].Message)
                    .Field("frame", StackFrames.OwnFrame(failed[i].StackTrace, Bridge.ProjectRoot))
                    .EndObject();
            }

            SessionState.SetString(ResultKey + id, json.EndArray().EndObject().ToString());
            SessionState.EraseString(RunningKey);
        }

        public void TestStarted(ITestAdaptor test)
        {
        }

        public void TestFinished(ITestResultAdaptor result)
        {
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
