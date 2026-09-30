using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>
    /// `/package/*` without a network call: the manifest is read from the local cache, arguments
    /// and refusals are checked before any request, and the poll plumbing is tested on its own.
    /// Adding and removing a real package is the end to end test's job, on a BridgeHost copy.
    /// </summary>
    public class PackageOpsTests : OpsFixture
    {
        [UnityTest]
        public IEnumerator List_answers_the_manifests_direct_dependencies_after_a_few_updates()
        {
            Func<Reply> poll = Routes.Handle(Request("/package/list"), new ConsoleBuffer());
            Reply reply = null;
            double until = EditorApplication.timeSinceStartup + 60;
            while ((reply = poll()) == null && EditorApplication.timeSinceStartup < until)
            {
                yield return null;
            }

            Assert.IsNotNull(reply, "the Package Manager gave no list within 60 s");
            StringAssert.StartsWith("{\"lines\":[", reply.Body);
            StringAssert.Contains("\"com.unity.test-framework  ", reply.Body);
            StringAssert.Contains("\"com.unitygraph.bridge  " + Bridge.Version + "  local  file:", reply.Body);
        }

        [Test]
        public void Add_and_remove_refuse_missing_arguments_before_any_request()
        {
            Assert.AreEqual(Err("no package id given: a name, name@version, a git URL or file:path"), Ask("/package/add"));
            Assert.AreEqual(Err("no package id given: a name, name@version, a git URL or file:path"), Ask("/package/add", "id", "  "));
            Assert.AreEqual(Err("no package name given"), Ask("/package/remove"));
            Assert.AreEqual(Err("no package name given"), Ask("/package/remove", "name", " "));
        }

        [TestCase("com.unitygraph.bridge")]
        [TestCase("COM.UnityGraph.Bridge")]
        [TestCase("com.unitygraph.bridge@0.2.0")]
        public void Removing_the_bridge_itself_is_refused(string name) =>
            Assert.AreEqual(
                Err("removing com.unitygraph.bridge would end this connection: remove it in Package Manager (Window > Package Manager)"),
                Ask("/package/remove", "name", name));

        [Test]
        public void Package_changes_are_refused_in_play_mode()
        {
            const string Message = "package changes reload scripts and end play mode: ug stop first";

            Assert.AreEqual(Err(Message), PackageOps.Add(Request("/package/add", "id", "com.unity.mathematics"), true)().Body);
            Assert.AreEqual(Err(Message), PackageOps.Remove(Request("/package/remove", "name", "com.unity.mathematics"), true)().Body);
        }

        [Test]
        public void A_poll_gives_null_until_the_request_completes_and_then_its_answer()
        {
            bool done = false;
            Func<Reply> poll = PackageOps.Poll(() => done, () => Answer.Line("added com.x  1.0.0  registry"));

            Assert.IsNull(poll());
            Assert.IsNull(poll());
            done = true;
            Assert.AreEqual(Lines("added com.x  1.0.0  registry"), poll().Body);
        }
    }
}
