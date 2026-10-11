using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityGraph.Bridge.Tests
{
    /// <summary>
    /// `ug ui` and the `--value` side of `ug click` (SPEC-play-input) on a canvas built in the test. They need a
    /// Game view size (a window); without one they are ignored and the end-to-end run covers them in play mode.
    /// </summary>
    public class UguiInputTests
    {
        private readonly List<GameObject> _made = new List<GameObject>();
        private GameObject _canvas;
        private EventSystem _events;

        [SetUp]
        public void SetUp()
        {
            if (!PlayInput.ScreenSize(out _, out _))
            {
                Assert.Ignore("the editor has no Game view size (no window)");
            }

            Assert.IsNotNull(PlayInput.Ui, "the Ugui assembly registers its provider");
            _canvas = Made(new GameObject("UgTestCanvas", typeof(Canvas)));
            _canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        }

        [TearDown]
        public void TearDown()
        {
            if (_events != null)
            {
                Lifecycle("OnDisable");
                _events = null;
            }

            foreach (GameObject made in _made)
            {
                if (made != null)
                {
                    Object.DestroyImmediate(made);
                }
            }

            _made.Clear();
        }

        private GameObject Made(GameObject go)
        {
            _made.Add(go);
            return go;
        }

        // A child of the canvas at a pixel position from the bottom left, 100x30.
        private GameObject Child(string name, float x, float y, params System.Type[] components)
        {
            var go = new GameObject(name, components.Length == 0 ? new[] { typeof(RectTransform) } : components);
            go.transform.SetParent(_canvas.transform, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(100, 30);
            rect.anchoredPosition = new Vector2(x, y);
            return go;
        }

        private GameObject Labelled(string name, string label, float x, float y, System.Type kind)
        {
            GameObject go = Child(name, x, y, typeof(Image), kind);
            GameObject text = new GameObject("Label", typeof(RectTransform), typeof(Text));
            text.transform.SetParent(go.transform, false);
            text.GetComponent<Text>().text = label;
            return go;
        }

        private void NeedEventSystem()
        {
            _events = Made(new GameObject("UgTestEvents")).AddComponent<EventSystem>();
            Lifecycle("OnEnable");
        }

        // An EventSystem registers itself in OnEnable, which edit mode does not run; the test runs it by hand.
        private void Lifecycle(string method) => typeof(EventSystem).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_events, null);

        private static List<string> List(bool all = false) => PlayInput.List(all).Lines.ToList();

        [Test]
        public void The_list_shows_kinds_labels_and_values_in_reading_order()
        {
            Labelled("Play", "Play", 200, 300, typeof(Button));
            GameObject sound = Labelled("Sound", "Sound", 200, 200, typeof(Toggle));
            sound.GetComponent<Toggle>().isOn = true;
            GameObject volume = Child("Volume", 200, 100, typeof(Image), typeof(Slider));
            volume.GetComponent<Slider>().value = 0.5f;

            List<string> lines = List();

            StringAssert.StartsWith("ui  ", lines[0]);
            StringAssert.Contains("1 canvas", lines[0]);
            StringAssert.Contains("no EventSystem", lines[0]);
            string[] rows = lines.Skip(1).Where(l => l.StartsWith("UgTestCanvas/")).ToArray();
            Assert.AreEqual(3, rows.Length, string.Join("\n", lines));
            StringAssert.StartsWith("UgTestCanvas/Play  button  \"Play\"  ", rows[0]);
            StringAssert.StartsWith("UgTestCanvas/Sound  toggle  \"Sound\"  on  ", rows[1]);
            StringAssert.StartsWith("UgTestCanvas/Volume  slider  0.5 (0-1)  ", rows[2]);
        }

        [Test]
        public void A_disabled_element_is_counted_by_default_and_listed_with_all()
        {
            Labelled("Play", "Play", 200, 300, typeof(Button));
            Labelled("Locked", "Locked", 200, 200, typeof(Button)).GetComponent<Button>().interactable = false;

            List<string> plain = List();
            List<string> all = List(true);

            CollectionAssert.DoesNotContain(plain.Select(l => l.Split(' ')[0]), "UgTestCanvas/Locked");
            StringAssert.Contains("1 more not usable (1 disabled): ug ui --all", plain.Last());
            Assert.IsTrue(all.Any(l => l.StartsWith("UgTestCanvas/Locked  button  \"Locked\"  ") && l.EndsWith("  disabled")), string.Join("\n", all));
        }

        [Test]
        public void A_long_list_is_cut_at_forty_lines_with_the_rest_counted()
        {
            for (int i = 0; i < 45; i++)
            {
                Labelled("Item" + i, "Item " + i, 100 + (i % 5) * 110, 20 + (i / 5) * 12, typeof(Button));
            }

            List<string> lines = List();

            Assert.AreEqual(1 + 40 + 1, lines.Count, string.Join("\n", lines));
            StringAssert.EndsWith("(+5 more: ug ui --all)", lines.Last());
        }

        [Test]
        public void No_canvas_is_said_in_the_head()
        {
            Object.DestroyImmediate(_canvas);

            StringAssert.Contains("no canvas", List()[0]);
        }

        [Test]
        public void Click_without_an_event_system_refuses()
        {
            Labelled("Play", "Play", 200, 300, typeof(Button));

            StringAssert.StartsWith("{\"error\":\"no EventSystem in the loaded scenes", PlayInput.Ui.Click("Play", null).Body);
        }

        [Test]
        public void Value_sets_a_slider_a_toggle_and_a_scrollbar_through_the_component()
        {
            NeedEventSystem();
            GameObject volume = Child("Volume", 200, 100, typeof(Image), typeof(Slider));
            volume.GetComponent<Slider>().value = 0.5f;
            GameObject sound = Labelled("Sound", "Sound", 200, 200, typeof(Toggle));
            GameObject bar = Child("Scroll", 200, 50, typeof(Image), typeof(Scrollbar));

            Assert.AreEqual("{\"lines\":[\"set UgTestCanvas/Volume (slider) 0.5 -> 0.25\"]}", PlayInput.Ui.Click("UgTestCanvas/Volume", "0.25").Body);
            Assert.AreEqual(0.25f, volume.GetComponent<Slider>().value);
            Assert.AreEqual("{\"lines\":[\"set UgTestCanvas/Sound (toggle) off -> on\"]}", PlayInput.Ui.Click("UgTestCanvas/Sound", "on").Body);
            Assert.IsTrue(sound.GetComponent<Toggle>().isOn);
            Assert.AreEqual("{\"lines\":[\"set UgTestCanvas/Scroll (scrollbar) 0 -> 1\"]}", PlayInput.Ui.Click("UgTestCanvas/Scroll", "1").Body);
            Assert.AreEqual(1f, bar.GetComponent<Scrollbar>().value);
        }

        [Test]
        public void Value_refusals_say_what_is_wrong()
        {
            NeedEventSystem();
            Child("Volume", 200, 100, typeof(Image), typeof(Slider));
            Labelled("Sound", "Sound", 200, 200, typeof(Toggle));
            Labelled("Play", "Play", 200, 300, typeof(Button));
            Labelled("Locked", "Locked", 200, 250, typeof(Slider)).GetComponent<Slider>().interactable = false;

            StringAssert.Contains("is outside UgTestCanvas/Volume's range 0-1", PlayInput.Ui.Click("UgTestCanvas/Volume", "7").Body);
            StringAssert.Contains("takes a number from 0 to 1, not loud", PlayInput.Ui.Click("UgTestCanvas/Volume", "loud").Body);
            StringAssert.Contains("a toggle takes on or off, not maybe", PlayInput.Ui.Click("UgTestCanvas/Sound", "maybe").Body);
            StringAssert.Contains("is a button: --value takes a slider", PlayInput.Ui.Click("UgTestCanvas/Play", "1").Body);
            StringAssert.Contains("is not interactable", PlayInput.Ui.Click("UgTestCanvas/Locked", "0.5").Body);
            StringAssert.Contains("--value operates an element's component: give an address or label, not a point", PlayInput.Ui.Click("100,100", "1").Body);
        }

        [Test]
        public void An_unknown_element_names_the_way_to_list_them()
        {
            NeedEventSystem();

            StringAssert.Contains("(ug ui lists the elements)", PlayInput.Ui.Click("Nothing Here", null).Body);
        }
    }
}
