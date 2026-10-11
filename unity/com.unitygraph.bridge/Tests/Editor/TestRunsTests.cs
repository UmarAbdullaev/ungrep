using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>
    /// `/tests` against the test runner's save-scenes step and runs that end without a result
    /// (SPEC-unity-loop, Modified scenes and stopped runs). The bridge's run keys are set aside for
    /// each test, so these pass inside a run `ug test` started too.
    /// </summary>
    public class TestRunsTests
    {
        // TestRuns' SessionState keys; its assembly is not visible here.
        private const string RunningKey = "unitygraph.tests.running";
        private const string LastKey = "unitygraph.tests.last";
        private const string ResultKey = "unitygraph.tests.result.";

        // A run id the test runner never had: a run it canceled looks the same.
        private const string Gone = "ug-tests-gone";
        private const string Canceled = "{\"state\":\"stopped\",\"reason\":\"canceled in Unity before it finished\"}";
        private const string Refused = "unsaved changes in untitled scene (the test runner would ask to save them): ug save or ug undo first";

        private string _running;
        private string _last;

        [SetUp]
        public void SetAside()
        {
            Assert.IsNotNull(Bridge.TestRuns, "the TestRuns assembly did not load");
            _running = SessionState.GetString(RunningKey, "");
            _last = SessionState.GetString(LastKey, "");
            SessionState.EraseString(RunningKey);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void PutBack()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.EraseString(ResultKey + Gone);
            PutBack(RunningKey, _running);
            PutBack(LastKey, _last);
        }

        [Test]
        public void A_modified_scene_refuses_the_run_and_starts_none()
        {
            Modify();

            string id = Bridge.TestRuns.Start("EditMode", "UgNothingMatchesThis", out string error);

            Assert.IsNull(id);
            Assert.AreEqual(Refused, error);
            Assert.AreEqual("", SessionState.GetString(RunningKey, ""));
            Assert.AreEqual(_last, SessionState.GetString(LastKey, ""));
        }

        [Test]
        public void A_run_the_test_runner_no_longer_has_answers_stopped()
        {
            SessionState.SetString(RunningKey, Gone);

            Assert.AreEqual(Canceled, Bridge.TestRuns.Result(Gone));
            Assert.AreEqual("", SessionState.GetString(RunningKey, ""));
            Assert.AreEqual(Canceled, Bridge.TestRuns.Result(""));
        }

        [Test]
        public void A_run_the_test_runner_no_longer_has_does_not_hold_up_the_next_one()
        {
            SessionState.SetString(RunningKey, Gone);
            Modify();

            // Past `a test run is in progress` to the next check; the modified scene keeps a real run from starting.
            Assert.IsNull(Bridge.TestRuns.Start("EditMode", "UgNothingMatchesThis", out string error));
            Assert.AreEqual(Refused, error);
            Assert.AreEqual(Canceled, Bridge.TestRuns.Result(""));
        }

        private static void Modify()
        {
            Undo.RegisterCreatedObjectUndo(new GameObject("Unsaved"), "Add Unsaved");
            Assert.IsTrue(SceneManager.GetActiveScene().isDirty);
        }

        private static void PutBack(string key, string value)
        {
            if (value.Length > 0)
            {
                SessionState.SetString(key, value);
            }
            else
            {
                SessionState.EraseString(key);
            }
        }
    }
}
