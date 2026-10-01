using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>`/event/*` and component references in `/set` (SPEC-editor-ops, Endpoints).</summary>
    public class EventOpsTests : OpsFixture
    {
        private const string Unsaved = "  (Main.unity modified, not saved)";
        private const string Player = "Level/Player";
        private const string PlayerEvents = "Level/Player:UgEvents";

        private UgEvents _events;

        [SetUp]
        public void AddEvents()
        {
            _events = Find(Player).AddComponent<UgEvents>();
            Find("Level/Enemy#1").AddComponent<UgEvents>();
        }

        // Calls ug writes are RuntimeOnly, as the inspector's default: switch them on so EditMode can invoke them.
        private void Arm()
        {
            var serialized = new SerializedObject(_events);
            foreach (string name in new[] { "fired", "changed" })
            {
                SerializedProperty calls = serialized.FindProperty(name + ".m_PersistentCalls.m_Calls");
                for (int i = 0; i < calls.arraySize; i++)
                {
                    SerializedProperty state = calls.GetArrayElementAtIndex(i).FindPropertyRelative("m_CallState");
                    Assert.AreEqual(2, state.enumValueIndex, "RuntimeOnly");
                    state.enumValueIndex = 1;
                }
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string Add(string eventName, string target, string method, string arg = null) =>
            arg == null
                ? Ask("/event/add", "object", Player, "event", eventName, "target", target, "method", method)
                : Ask("/event/add", "object", Player, "event", eventName, "target", target, "method", method, "arg", arg);

        [Test]
        public void A_void_method_is_called_when_the_event_fires()
        {
            Assert.AreEqual(Lines("Level/Player  UgEvents.fired  + Level/Player:UgEvents.Ping()" + Unsaved), Add("fired", PlayerEvents, "Ping"));

            Assert.AreEqual(Lines("Level/Player  UgEvents.fired", "  0  Level/Player:UgEvents.Ping()"), Ask("/event/list", "object", Player));

            Arm();
            _events.fired.Invoke();
            Assert.AreEqual(1, _events.pings);
        }

        [Test]
        public void A_game_object_target_finds_the_component_with_the_method()
        {
            Assert.AreEqual(Lines("Level/Player  UgEvents.fired  + Level/Player:UgEvents.Ping()" + Unsaved), Add("fired", Player, "Ping"));
            Assert.AreEqual(Lines("Level/Player  UgEvents.fired  + Level/Enemy#1:UgEvents.Ping()" + Unsaved), Add("fired", "Level/Enemy#1", "Ping"));
        }

        [Test]
        public void A_method_taking_the_events_argument_gets_it_passed_on()
        {
            Assert.AreEqual(
                Lines("Level/Player  UgEvents.changed  + Level/Player:UgEvents.SetLevel(event argument)" + Unsaved),
                Add("changed", PlayerEvents, "SetLevel"));

            Arm();
            _events.changed.Invoke(0.25f);
            Assert.AreEqual(0.25f, _events.level);
        }

        [Test]
        public void A_static_argument_is_stored_by_type()
        {
            Assert.AreEqual(Lines("Level/Player  UgEvents.fired  + Level/Player:UgEvents.SetLevel(0.5)" + Unsaved), Add("fired", PlayerEvents, "SetLevel", "0.5"));
            Add("fired", PlayerEvents, "Say", "hi");
            Add("fired", PlayerEvents, "Flag", "true");
            Assert.AreEqual(
                Lines("Level/Player  UgEvents.fired  + Level/Player:UgEvents.Pick(Level/Enemy#1)" + Unsaved),
                Add("fired", PlayerEvents, "Pick", "Level/Enemy#1"));

            Arm();
            _events.fired.Invoke();
            Assert.AreEqual(0.5f, _events.level);
            Assert.AreEqual("hi", _events.said);
            Assert.IsTrue(_events.flag);
            Assert.AreSame(Find("Level/Enemy#1"), _events.picked);
        }

        [Test]
        public void Overloads_are_picked_by_whether_an_argument_is_given()
        {
            Add("fired", PlayerEvents, "Both");
            Add("fired", PlayerEvents, "Both", "3");

            Arm();
            _events.fired.Invoke();
            Assert.AreEqual(13, _events.pings);
        }

        [Test]
        public void A_game_objects_own_method_and_a_property_setter_work()
        {
            Assert.AreEqual(Lines("Level/Player  UgEvents.fired  + Level/Enemy#2.SetActive(false)" + Unsaved), Add("fired", "Level/Enemy#2", "SetActive", "false"));
            Assert.AreEqual(Lines("Level/Player  UgEvents.fired  + Level/Player:UgEvents.set_enabled(false)" + Unsaved), Add("fired", PlayerEvents, "enabled", "false"));

            Arm();
            _events.fired.Invoke();
            Assert.IsFalse(Find("Level/Enemy#2").activeSelf);
            Assert.IsFalse(_events.enabled);
        }

        [Test]
        public void Remove_takes_one_call_and_undo_brings_it_back()
        {
            Add("fired", PlayerEvents, "Ping");
            Add("fired", PlayerEvents, "Both");

            Assert.AreEqual(
                Err("Level/Player  UgEvents.fired has 2 calls: give an index from 0 to 1 (ug event list)"),
                Ask("/event/remove", "object", Player, "event", "fired"));
            Assert.AreEqual(
                Lines("Level/Player  UgEvents.fired  - Level/Player:UgEvents.Ping()" + Unsaved),
                Ask("/event/remove", "object", Player, "event", "fired", "index", "0"));
            Assert.AreEqual(
                Lines("Level/Player  UgEvents.fired  - Level/Player:UgEvents.Both()" + Unsaved),
                Ask("/event/remove", "object", Player, "event", "fired"));
            Assert.AreEqual(Err("Level/Player  UgEvents.fired has no calls"), Ask("/event/remove", "object", Player, "event", "fired"));

            Undo.PerformUndo();
            Undo.PerformUndo();
            Assert.AreEqual(Lines("Level/Player  UgEvents.fired", "  0  Level/Player:UgEvents.Ping()", "  1  Level/Player:UgEvents.Both()"), Ask("/event/list", "object", Player, "event", "fired"));
        }

        [Test]
        public void An_add_is_one_undo_group()
        {
            Add("fired", PlayerEvents, "Ping");
            Undo.PerformUndo();

            Assert.AreEqual(Err("no event has calls on Level/Player"), Ask("/event/list", "object", Player));
            Assert.AreEqual(Lines("Level/Player  UgEvents.fired  no calls"), Ask("/event/list", "object", Player, "event", "fired", "component", "UgEvents"));
        }

        [Test]
        public void Events_inside_a_prefab_asset_are_written_at_once_and_stay_inside_it()
        {
            Ask("/component/add", "object", PrefabPath, "type", "UnityGraph.Bridge.Tests.UgEvents");
            string reply = Ask("/event/add", "object", PrefabPath, "event", "fired", "target", PrefabPath + ":UgEvents", "method", "Ping");

            Assert.AreEqual(Lines(PrefabPath + "  UgEvents.fired  + " + PrefabPath + ":UgEvents.Ping()  (written " + PrefabPath + ")"), reply);
            Assert.AreEqual(1, AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath).GetComponent<UgEvents>().fired.GetPersistentEventCount());
            Assert.AreEqual(
                Err("a prefab asset can only call methods on its own objects: write the target as " + PrefabPath + ":<object>:<Component>"),
                Ask("/event/add", "object", PrefabPath, "event", "fired", "target", PlayerEvents, "method", "Ping"));
        }

        [Test]
        public void Refusals_say_what_to_change()
        {
            Find(Player).AddComponent<UgEvents>();
            Assert.AreEqual(
                Err("ambiguous: fired is on UgEvents#1, UgEvents#2: give --component"),
                Add("fired", PlayerEvents + "#1", "Ping"));
            Assert.AreEqual(
                Err("no event click on Level/Enemy#2 (no events on its components)"),
                Ask("/event/add", "object", "Level/Enemy#2", "event", "click", "target", Player, "method", "Ping"));
            Assert.AreEqual(Err("no object given"), Ask("/event/add", "event", "fired", "target", Player, "method", "Ping"));
            Assert.AreEqual(Err("no target given"), Ask("/event/add", "object", Player, "event", "fired", "method", "Ping", "component", "UgEvents#1"));
            Assert.AreEqual(Err("no method given"), Ask("/event/add", "object", Player, "event", "fired", "target", Player));
            Assert.AreEqual(Err("no event given"), Ask("/event/add", "object", Player, "target", Player, "method", "Ping"));
            Assert.AreEqual(
                Err("Packages/com.example/Thing.prefab is in a package, which ug does not edit: copy it into Assets/ first"),
                Ask("/event/add", "object", "Packages/com.example/Thing.prefab", "event", "fired", "target", Player, "method", "Ping"));
        }

        [Test]
        public void Refusals_about_the_method()
        {
            string Wired(string method, string arg = null) =>
                arg == null
                    ? Ask("/event/add", "object", "Level/Enemy#1", "event", "fired", "target", "Level/Enemy#1:UgEvents", "method", method)
                    : Ask("/event/add", "object", "Level/Enemy#1", "event", "fired", "target", "Level/Enemy#1:UgEvents", "method", method, "arg", arg);

            StringAssert.StartsWith("{\"error\":\"UgEvents has no public void method Nope (its own: ", Wired("Nope"));
            StringAssert.StartsWith("{\"error\":\"UgEvents has no public void method Value (its own: ", Wired("Value"));
            Assert.AreEqual(Err("UgEvents.SetLevel takes a Single: give --arg"), Wired("SetLevel"));
            Assert.AreEqual(Err("UgEvents.Ping takes no int, float, string, bool or object argument: drop --arg"), Wired("Ping", "1"));
            Assert.AreEqual(Err("UgEvents.Wide has parameters an event cannot pass (Int32, Int32)"), Wired("Wide"));
            Assert.AreEqual(Err("--arg: not a number: fast"), Wired("SetLevel", "fast"));
            Assert.AreEqual(
                Err("Level/Enemy#1 has no method Nope (components: Transform, UgEvents)"),
                Ask("/event/add", "object", "Level/Enemy#1", "event", "fired", "target", "Level/Enemy#1", "method", "Nope"));
        }

        [Test]
        public void Set_takes_a_component_address_as_a_reference()
        {
            Assert.AreEqual(
                Lines("Level/Player  UgEvents.picked  null -> Level/Enemy#1" + Unsaved),
                Ask("/set", "object", Player, "component", "UgEvents", "field", "picked", "value", "Level/Enemy#1:UgEvents"));
            Assert.AreSame(Find("Level/Enemy#1"), _events.picked);

            Assert.AreEqual(
                Lines("Level/Player  UgProbe.target  null -> Level/Enemy#1" + Unsaved),
                Ask("/set", "object", Player, "field", "target", "value", "Level/Enemy#1:Transform"));
            Assert.AreEqual(
                Err("Level/Player  UgProbe.target: Level/Enemy#1 has no Rigidbody (has: Transform, UgEvents)"),
                Ask("/set", "object", Player, "field", "target", "value", "Level/Enemy#1:Rigidbody"));
        }
    }
}
