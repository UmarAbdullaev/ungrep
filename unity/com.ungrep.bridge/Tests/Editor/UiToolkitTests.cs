using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ungrep.Bridge.Tests
{
    /// <summary>
    /// UI Toolkit in `ug ui`, `ug click --value`, `ug nav`, `ug tree --ui`, `ug inspect` and `ug set` (SPEC-play-input,
    /// UI Toolkit) on a document built from a UXML file in edit mode, where the bridge lays the panel out itself.
    /// The panel has no theme, so every size comes from the inline styles. What needs a Game view size is ignored
    /// without one; clicks and focus moves in play mode are in the end-to-end run.
    /// </summary>
    public class UiToolkitTests : OpsFixture
    {
        private const string UxmlPath = Folder + "/Menu.uxml";
        private const string ThemeFolder = "Assets/UI Toolkit";

        private const string Uxml =
            "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n"
            + "  <ui:VisualElement name=\"Panel\" style=\"position: absolute; left: 10px; top: 10px; width: 300px;\">\n"
            + "    <ui:Button name=\"Play\" text=\"Play\" class=\"primary wide\" style=\"height: 30px; margin: 0; padding: 0; border-width: 0; background-color: rgb(255, 0, 0);\" />\n"
            + "    <ui:Button text=\"Unnamed\" style=\"height: 30px; margin: 0; border-width: 0;\" />\n"
            + "    <ui:Button text=\"Other\" style=\"height: 30px; margin: 0; border-width: 0;\" />\n"
            + "    <ui:Toggle name=\"Sound\" label=\"Sound\" value=\"true\" style=\"height: 20px; margin: 0;\" />\n"
            + "    <ui:Slider name=\"Volume\" label=\"Volume\" low-value=\"0\" high-value=\"1\" value=\"0.8\" style=\"height: 20px; margin: 0;\" />\n"
            + "    <ui:TextField name=\"Player\" label=\"Name\" value=\"Bob\" style=\"height: 20px; margin: 0;\" />\n"
            + "    <ui:DropdownField name=\"Quality\" label=\"Quality\" choices=\"Low,Medium,High\" index=\"2\" style=\"height: 20px; margin: 0;\" />\n"
            + "    <ui:Button name=\"Locked\" text=\"Locked\" style=\"height: 30px; margin: 0;\" />\n"
            + "    <ui:Button name=\"Ghost\" text=\"Ghost\" style=\"height: 30px; margin: 0; visibility: hidden;\" />\n"
            + "  </ui:VisualElement>\n"
            + "  <ui:VisualElement name=\"Cover\" style=\"position: absolute; left: 10px; top: 70px; width: 300px; height: 30px;\" />\n"
            + "</ui:UXML>\n";

        private PanelSettings _settings;
        private UIDocument _document;
        private bool _hadThemes;

        [SetUp]
        public void BuildDocument()
        {
            _hadThemes = AssetDatabase.IsValidFolder(ThemeFolder);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), UxmlPath), Uxml);
            AssetDatabase.ImportAsset(UxmlPath);
            _settings = ScriptableObject.CreateInstance<PanelSettings>();
            _settings.name = "TestPanel";
            _settings.hideFlags = HideFlags.HideAndDontSave;
            _document = new GameObject("Menu").AddComponent<UIDocument>();
            _document.panelSettings = _settings;
            _document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            Assert.IsNull(UiToolkit.Layout(), "this Unity lays runtime panels out in edit mode");
            _document.rootVisualElement.Q("Locked").SetEnabled(false);
        }

        [TearDown]
        public void DropDocument()
        {
            if (_document != null)
            {
                Object.DestroyImmediate(_document.gameObject);
            }

            Object.DestroyImmediate(_settings);
            if (!_hadThemes && AssetDatabase.IsValidFolder(ThemeFolder))
            {
                AssetDatabase.DeleteAsset(ThemeFolder);
            }
        }

        private static void NeedScreen()
        {
            if (!PlayInput.ScreenSize(out _, out _))
            {
                Assert.Ignore("the editor has no Game view size (no window)");
            }
        }

        private static UiToolkit.Target Target(string address)
        {
            UiToolkit.Target target = UiToolkit.Find(address, out string error);
            Assert.IsNull(error, error);
            Assert.IsNotNull(target, address);
            return target;
        }

        private static string FindError(string address)
        {
            Assert.IsNull(UiToolkit.Find(address, out string error));
            return error;
        }

        private T Element<T>(string name) where T : VisualElement => _document.rootVisualElement.Q<T>(name);

        [Test]
        public void The_list_has_document_addresses_kinds_values_and_screen_rects()
        {
            NeedScreen();

            List<string> lines = PlayInput.List(false).Lines.ToList();

            StringAssert.StartsWith("ui  ", lines[0]);
            StringAssert.Contains("edit mode  1 UI Toolkit document", lines[0]);
            string[] rows = lines.Where(l => l.StartsWith("Menu/")).ToArray();
            Assert.AreEqual("Menu/Play  button  \"Play\"  .primary .wide  160,25  300x30", rows[0], string.Join("\n", lines));
            Assert.AreEqual("Menu/Button#2  button  \"Unnamed\"  160,55  300x30", rows[1], string.Join("\n", lines));
            StringAssert.StartsWith("Menu/Sound  toggle  \"Sound\"  on  160,110  300x20", rows[2]);
            StringAssert.StartsWith("Menu/Volume  slider  0.8 (0-1)  ", rows[3]);
            StringAssert.StartsWith("Menu/Player  input  \"Bob\"  ", rows[4]);
            StringAssert.StartsWith("Menu/Quality  dropdown  \"High\" (3 options)  ", rows[5]);
            Assert.AreEqual(6, rows.Length, string.Join("\n", lines));
            StringAssert.StartsWith("  3 more not usable (", lines.Last());
            StringAssert.Contains("1 blocked", lines.Last());
            StringAssert.Contains("1 disabled", lines.Last());
            StringAssert.Contains("1 hidden", lines.Last());
        }

        [Test]
        public void All_lists_what_stops_a_player()
        {
            NeedScreen();

            List<string> lines = PlayInput.List(true).Lines.ToList();

            Assert.IsTrue(lines.Any(l => l.StartsWith("Menu/Button#3  button  \"Other\"  ") && l.EndsWith("  blocked by Menu/Cover")), string.Join("\n", lines));
            Assert.IsTrue(lines.Any(l => l.StartsWith("Menu/Locked  button  \"Locked\"  ") && l.EndsWith("  disabled")), string.Join("\n", lines));
            Assert.IsTrue(lines.Any(l => l.StartsWith("Menu/Ghost  button  \"Ghost\"  ") && l.EndsWith("  hidden")), string.Join("\n", lines));
        }

        [Test]
        public void A_document_without_panel_settings_is_counted_not_listed()
        {
            NeedScreen();
            new GameObject("Bare").AddComponent<UIDocument>();

            StringAssert.Contains("1 UI Toolkit document not shown (no PanelSettings or UXML)", PlayInput.List(false).Lines[0]);
            Assert.AreEqual(Err("Bare's UIDocument has no panel (no PanelSettings or UXML)"), Ask("/tree", "ui", "Bare"));
        }

        [Test]
        public void Addresses_are_the_shortest_that_resolve_back()
        {
            Assert.AreEqual("Menu/Play", Target("Menu/Play").Address);
            Assert.AreEqual("Menu/Play", Target("Menu/Panel/Play").Address);
            Assert.AreEqual("Menu/Play", Target("Menu/Button.primary").Address);
            Assert.AreEqual("Menu/Play", Target("Menu/.wide").Address);
            Assert.AreEqual("Unnamed", ((Button)Target("Menu/Button#2").Element).text);
            Assert.AreEqual("Menu/Button#2", Target("Menu/Panel/Button#2").Address);
            Assert.AreEqual("Menu/Volume", Target("menu/volume").Address);

            StringAssert.StartsWith("not found: Plya in Menu's UI Toolkit document", FindError("Menu/Plya"));
            StringAssert.StartsWith("ambiguous: Button (5 in Menu's UI Toolkit document): Menu/Play, Menu/Button#2", FindError("Menu/Button"));
            StringAssert.StartsWith("not found: Button#9 in Menu's UI Toolkit document", FindError("Menu/Button#9"));
            Assert.IsNull(UiToolkit.Find("Level/Player/Model", out string none));
            Assert.IsNull(none, "a game object path without a UIDocument is the caller's to answer");
        }

        [Test]
        public void Game_objects_win_and_a_miss_offers_near_names()
        {
            new GameObject("Play").transform.SetParent(_document.transform, false);

            StringAssert.DoesNotContain("UnityEngine.UIElements.Button", Ask("/inspect", "object", "Menu/Play"), "a child object named Play is what Menu/Play names");
            Assert.AreEqual("not found: Plya in Menu's UI Toolkit document (near: Play)", FindError("Menu/Plya"));
        }

        [Test]
        public void A_nested_document_is_its_own_and_not_repeated_under_its_parent()
        {
            NeedScreen();
            UIDocument nested = InnerDocument();

            Assert.AreSame(nested.parentUI, _document, "Unity nests a child object's document");
            Assert.AreEqual("Menu/Inner/Help", Target("Menu/Inner/Help").Address);
            StringAssert.StartsWith("not found: Help in Menu's UI Toolkit document", FindError("Menu/Help"));
            string[] rows = PlayInput.List(true).Lines.Where(l => l.Contains("\"Help\"")).ToArray();
            Assert.AreEqual(1, rows.Length, string.Join("\n", rows));
            StringAssert.StartsWith("Menu/Inner/Help  button  \"Help\"  ", rows[0]);
            string[] tree = Split(Ask("/tree", "ui", "Menu"));
            Assert.IsTrue(tree.Any(l => l.EndsWith("  (its own UIDocument)")), string.Join("\n", tree));
            Assert.IsFalse(tree.Any(l => l.TrimStart().StartsWith("Help  ")), string.Join("\n", tree));
        }

        [Test]
        public void Display_none_is_left_out_until_set_back()
        {
            NeedScreen();

            StringAssert.Contains("display  flex -> none", Ask("/set", "object", "Menu/Locked", "field", "display", "value", "none"));
            Assert.IsFalse(PlayInput.List(true).Lines.Any(l => l.StartsWith("Menu/Locked")), "display: none is not on screen at all");
            Ask("/set", "object", "Menu/Locked", "field", "display", "value", "flex");
            Assert.IsTrue(PlayInput.List(true).Lines.Any(l => l.StartsWith("Menu/Locked")));
        }

        [Test]
        public void Setting_the_uxml_rebuilds_the_document()
        {
            WriteUxml(InnerPath, InnerUxml);

            StringAssert.Contains("UIDocument.sourceAsset", Ask("/set", "object", "Menu", "field", "sourceAsset", "value", InnerPath, "component", "UIDocument"));
            Assert.AreEqual("Menu/Help", Target("Menu/Help").Address, "the document shows the new UXML without a scene reload");
            StringAssert.StartsWith("not found: Play", FindError("Menu/Play"));
        }

        private const string InnerPath = Folder + "/Inner.uxml";
        private const string InnerUxml = "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n  <ui:Button name=\"Help\" text=\"Help\" style=\"height: 30px; margin: 0;\" />\n</ui:UXML>\n";

        private static void WriteUxml(string path, string text)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Application.dataPath), path), text);
            AssetDatabase.ImportAsset(path);
        }

        private UIDocument InnerDocument()
        {
            WriteUxml(InnerPath, InnerUxml);
            var inner = new GameObject("Inner");
            inner.transform.SetParent(_document.transform, false);
            UIDocument nested = inner.AddComponent<UIDocument>();
            nested.panelSettings = _settings;
            nested.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(InnerPath);
            Assert.IsNull(UiToolkit.Layout());
            return nested;
        }

        [Test]
        public void Tree_ui_shows_the_document_then_its_elements()
        {
            string[] lines = Split(Ask("/tree", "ui", "Menu"));

            Assert.AreEqual("Menu  UIDocument  " + UxmlPath + "  PanelSettings \"TestPanel\"  sort 0", lines[0]);
            Assert.IsTrue(lines.Any(l => l.TrimStart().StartsWith("Panel  VisualElement")), string.Join("\n", lines));
            Assert.IsTrue(lines.Any(l => l.TrimStart().StartsWith("Play  Button  .primary .wide  \"Play\"")), string.Join("\n", lines));
            Assert.IsTrue(lines.Any(l => l.TrimStart().StartsWith("Locked  Button  \"Locked\"") && l.EndsWith("disabled")), string.Join("\n", lines));
            Assert.IsTrue(lines.Any(l => l.TrimStart().StartsWith("Ghost  Button  \"Ghost\"") && l.EndsWith("hidden")), string.Join("\n", lines));
            Assert.IsTrue(lines.Any(l => l.TrimStart().StartsWith("Volume  Slider") && l.Contains(" elements inside)")), "controls are closed: " + string.Join("\n", lines));

            string[] play = Split(Ask("/tree", "ui", "Menu/Volume", "depth", "1"));
            StringAssert.StartsWith("Menu/Volume  Slider", play[0]);
            Assert.Greater(play.Length, 1, "the given control is opened");

            Assert.AreEqual(Err("Level has no UIDocument"), Ask("/tree", "ui", "Level"));
        }

        [Test]
        public void Inspect_shows_an_elements_state_and_styles()
        {
            NeedScreen();

            string body = Ask("/inspect", "object", "Menu/Play");

            StringAssert.Contains("Menu/Play  UnityEngine.UIElements.Button  \\\"Play\\\"  (Menu: " + UxmlPath + ")", body);
            StringAssert.Contains("classes  .unity-text-element .unity-button .primary .wide", body);
            StringAssert.Contains("place  160,25  300x30  (panel 10,10 300x30)", body);
            StringAssert.Contains("look  background-color rgb(255, 0, 0)", body);
            Assert.AreEqual(Err("not found: Nothing in the loaded scenes"), Ask("/inspect", "object", "Nothing"));
        }

        [Test]
        public void A_cut_label_says_so_and_inspect_shows_texts_and_options_whole()
        {
            const string Long = "Start a new game from the very first level";
            Element<Button>("Play").text = Long + "\nnow";
            Element<Button>("Play").tooltip = "Begins again: " + Long;
            Element<TextField>("Player").value = Long;
            Element<DropdownField>("Quality").choices = new List<string> { "Low", "Medium", "High", "Ultra", "Epic", "Cinematic", "Custom" };

            Assert.AreEqual(UiItem.CutNote, Split(Ask("/tree", "ui", "Menu")).Last());

            List<string> inspect = UiToolkit.Inspect(Target("Menu/Play")).Lines.ToList();
            StringAssert.StartsWith("Menu/Play  UnityEngine.UIElements.Button  \"" + Long + "\\nnow\"  (Menu: ", inspect[0]);
            CollectionAssert.Contains(inspect, "tooltip  \"Begins again: " + Long + "\"");
            Assert.LessOrEqual(inspect.Count, 16, string.Join("\n", inspect));
            CollectionAssert.Contains(UiToolkit.Inspect(Target("Menu/Player")).Lines.ToList(), "value  \"" + Long + "\"");
            CollectionAssert.Contains(UiToolkit.Inspect(Target("Menu/Quality")).Lines.ToList(), "value  \"High\"  options Low, Medium, High, Ultra, Epic, Cinematic, Custom");
            Assert.AreEqual(Err("Menu/Quality has no option Nope (options: Low, Medium, High, Ultra, Epic, Cinematic, Custom)"), UiToolkit.Click(Target("Menu/Quality"), "Nope").Body);

            VisualElement panel = _document.rootVisualElement.Q("Panel");
            for (int i = 0; i < 12; i++)
            {
                panel.Add(new Label("x") { name = "Dup" });
            }

            StringAssert.EndsWith(", ... 2 more: ug tree --ui Menu/Panel", FindError("Menu/Panel/Dup"));

            NeedScreen();
            List<string> lines = PlayInput.List(false).Lines.ToList();
            Assert.AreEqual("  " + UiItem.CutNote, lines.Last(), string.Join("\n", lines));
            Element<Button>("Play").text = "Play";
            Element<TextField>("Player").value = "Bob";
            Assert.IsFalse(PlayInput.List(false).Lines.Contains("  " + UiItem.CutNote));
        }

        [Test]
        public void All_from_pages_past_its_lines_and_says_where_the_list_ends()
        {
            NeedScreen();

            List<string> all = PlayInput.List(true).Lines.ToList();
            List<string> rest = PlayInput.List(true, 2).Lines.ToList();

            Assert.AreEqual(all[0], rest[0], "the headline stays");
            CollectionAssert.AreEqual(all.Skip(3).ToList(), rest.Skip(1).ToList());
            int elements = all.Count - 1;
            Assert.AreEqual("  (nothing from element 50: " + elements + " elements)", PlayInput.List(true, 50).Lines.Last());
        }

        [Test]
        public void Set_changes_the_live_element_and_says_it_is_not_kept()
        {
            const string Keep = "  (live element only: not saved, no undo; edit " + UxmlPath + " or .uss to keep it)";

            Assert.AreEqual(Lines("Menu/Play  text  \"Play\" -> \"Go\"" + Keep), Ask("/set", "object", "Menu/Play", "field", "text", "value", "Go"));
            Assert.AreEqual("Go", Element<Button>("Play").text);

            StringAssert.Contains(Keep, Ask("/set", "object", "Menu/Play", "field", "class", "value", "+big -wide"));
            Assert.IsTrue(Element<Button>("Play").ClassListContains("big"));
            Assert.IsFalse(Element<Button>("Play").ClassListContains("wide"));

            StringAssert.Contains(Keep, Ask("/set", "object", "Menu/Play", "field", "style.width", "value", "50%"));
            Assert.AreEqual(new Length(50, LengthUnit.Percent), Element<Button>("Play").style.width.value);

            StringAssert.Contains(Keep, Ask("/set", "object", "Menu/Play", "field", "style.background-color", "value", "#ff0000"));
            Assert.AreEqual(Color.red, Element<Button>("Play").style.backgroundColor.value);

            StringAssert.Contains(Keep, Ask("/set", "object", "Menu/Play", "field", "enabled", "value", "false"));
            Assert.IsFalse(Element<Button>("Play").enabledSelf);

            StringAssert.Contains(Keep, Ask("/set", "object", "Menu/Volume", "field", "value", "value", "0.25"));
            Assert.AreEqual(0.25f, Element<Slider>("Volume").value);

            StringAssert.StartsWith("{\"error\":\"no style colr", Ask("/set", "object", "Menu/Play", "field", "style.colr", "value", "red"));
            StringAssert.StartsWith("{\"error\":\"not found: Plya in Menu's UI Toolkit document", Ask("/set", "object", "Menu/Plya", "field", "text", "value", "x"));
        }

        [Test]
        public void Value_sets_toggles_sliders_inputs_and_dropdowns()
        {
            Assert.AreEqual(Lines("set Menu/Sound (toggle) on -> off"), UiToolkit.Click(Target("Menu/Sound"), "off").Body);
            Assert.IsFalse(Element<Toggle>("Sound").value);
            Assert.AreEqual(Lines("set Menu/Volume (slider) 0.8 -> 0.5"), UiToolkit.Click(Target("Menu/Volume"), "0.5").Body);
            Assert.AreEqual(0.5f, Element<Slider>("Volume").value);
            Assert.AreEqual(Lines("set Menu/Player (input) \"Bob\" -> \"Alice\""), UiToolkit.Click(Target("Menu/Player"), "Alice").Body);
            Assert.AreEqual("Alice", Element<TextField>("Player").value);
            Assert.AreEqual(Lines("set Menu/Quality (dropdown) \"High\" -> \"Low\""), UiToolkit.Click(Target("Menu/Quality"), "low").Body);
            Assert.AreEqual("Low", Element<DropdownField>("Quality").value);
        }

        [Test]
        public void Value_refusals_say_what_is_wrong()
        {
            Assert.AreEqual(Err("Menu/Quality has no option Nope (options: Low, Medium, High)"), UiToolkit.Click(Target("Menu/Quality"), "Nope").Body);
            Assert.AreEqual(Err("Menu/Play is a button: --value takes a toggle, radio, foldout, slider, scrollbar, input, dropdown or field"), UiToolkit.Click(Target("Menu/Play"), "1").Body);
            Assert.AreEqual(Err("Menu/Sound takes on or off, not maybe"), UiToolkit.Click(Target("Menu/Sound"), "maybe").Body);
            StringAssert.StartsWith("{\"error\":\"Menu/Volume takes ", UiToolkit.Click(Target("Menu/Volume"), "7").Body);
            Element<Slider>("Volume").SetEnabled(false);
            Assert.AreEqual(Err("Menu/Volume is disabled: a player could not set it"), UiToolkit.Click(Target("Menu/Volume"), "0.5").Body);
        }

        [Test]
        public void A_click_is_refused_where_something_else_is_on_top()
        {
            NeedScreen();

            Assert.AreEqual(Err("blocked: Menu/Cover is on top of Menu/Button#3 at 160,85"), UiToolkit.Click(Target("Menu/Button#3"), null).Body);
        }

        [Test]
        public void A_click_reaches_the_button_through_the_panel()
        {
            NeedScreen();
            int clicked = 0;
            Element<Button>("Play").clicked += () => clicked++;

            Assert.AreEqual(Lines("clicked Menu/Play (button \"Play\") at 160,25"), PlayInput.Click("Menu/Play", null).Body);
            Assert.AreEqual(Lines("clicked Menu/Play (button \"Play\") at 160,25"), PlayInput.Click("160,25", null).Body);
            Assert.AreEqual(Lines("clicked Menu/Play (button \"Play\") at 160,25"), PlayInput.Click("Play", null).Body);
            Assert.AreEqual(3, clicked);
        }

        [Test]
        public void Nav_moves_the_focus_and_submits()
        {
            int clicked = 0;
            Element<Button>("Play").clicked += () => clicked++;

            Assert.AreEqual(Lines("nav down: Menu/Play -> Menu/Button#2"), UiToolkit.Nav("down", Target("Menu/Play"), true).Body);
            Assert.AreEqual("Menu/Button#2", UiToolkit.Focused()?.Address);
            Assert.AreEqual(Lines("nav submit on Menu/Play (button \"Play\")"), UiToolkit.Nav("submit", Target("Menu/Play"), true).Body);
            Assert.AreEqual(1, clicked);
            Assert.AreEqual(Err("Menu/Locked cannot take the focus (not focusable, disabled or hidden)"), UiToolkit.Nav("down", Target("Menu/Locked"), true).Body);
        }

        [Test]
        public void Nav_needs_a_known_direction_before_play_mode()
        {
            Assert.AreEqual(Err("ug nav takes submit, cancel, up, down, left, right, next, previous, not sideways"), PlayInput.UiNav(Request("/ui/nav", "direction", "sideways"))().Body);
            Assert.AreEqual(Err("not playing: ug play first"), PlayInput.UiNav(Request("/ui/nav", "direction", "down"))().Body);
        }

        [Test]
        public void A_ui_toolkit_document_is_not_a_click_target_itself()
        {
            NeedScreen();

            Assert.AreEqual(Err("Menu is a UI Toolkit document: give one of its elements (ug ui lists them)"), PlayInput.Click("Menu", null).Body);
        }

        // Shots (SPEC-play-input, UI Toolkit in shots): need a graphics device.

        private static bool HasGraphics => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;

        private static Texture2D Png(string name)
        {
            var image = new Texture2D(2, 2);
            Assert.IsTrue(image.LoadImage(File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "ungrep", "shots", name))));
            return image;
        }

        [Test]
        public void Shots_draw_the_document_in_the_game_view_alone_and_from_its_uxml()
        {
            if (!HasGraphics)
            {
                Assert.Ignore("no graphics device (-nographics)");
            }

            StringAssert.StartsWith("{\"lines\":[\"Library/ungrep/shots/game.png  960x540  ", Ask("/shot", "view", "game"));
            StringAssert.Contains("(Game view, no camera, 1 UI Toolkit document)", Ask("/shot", "view", "game"));
            StringAssert.Contains("(UI Toolkit document Menu)", Ask("/shot", "view", "Menu"));
            foreach (string name in new[] { "game.png", "ui-Menu.png" })
            {
                Texture2D image = Png(name);
                Color play = image.GetPixel(30, image.height - 25);
                Assert.Greater(play.r, 0.9f, name + ": the red Play button is drawn");
                Assert.Less(play.g, 0.1f, name);
                Assert.Less(image.GetPixel(800, image.height - 400).r, 0.5f, name + ": nothing is drawn away from the panel");
                Object.DestroyImmediate(image);
            }

            StringAssert.Contains("(UXML " + UxmlPath + ", ", Ask("/shot", "view", UxmlPath, "width", "320", "height", "240"));
            Texture2D uxml = Png("ui-Menu.png");
            Assert.AreEqual(320, uxml.width);
            Assert.Greater(uxml.GetPixel(30, uxml.height - 25).r, 0.9f, "the UXML shot draws the same layout");
            Object.DestroyImmediate(uxml);
            Assert.IsNull(_settings.targetTexture, "the PanelSettings gets its target back");
            Assert.IsNull(GameObject.Find("ug-shot-ui"), "the UXML shot's document is gone");
        }

        [Test]
        public void A_panel_without_a_uxml_and_a_style_sheet_are_refused()
        {
            Assert.AreEqual(Err("--panel goes with a .uxml path: ug shot Assets/UI/Menu.uxml --panel Assets/UI/Panel.asset"), Ask("/shot", "view", "game", "panel", Folder + "/Panel.asset"));
            if (HasGraphics)
            {
                Assert.AreEqual(Err("no PanelSettings at " + Folder + "/Panel.asset"), Ask("/shot", "view", UxmlPath, "panel", Folder + "/Panel.asset"));
                Assert.AreEqual(Err("no UXML at " + Folder + "/None.uxml"), Ask("/shot", "view", Folder + "/None.uxml"));
            }
        }

        private static string[] Split(string body)
        {
            StringAssert.StartsWith("{\"lines\":[", body);
            return body.Substring("{\"lines\":[\"".Length, body.Length - "{\"lines\":[\"".Length - "\"]}".Length)
                .Split(new[] { "\",\"" }, System.StringSplitOptions.None)
                .Select(l => l.Replace("\\\"", "\""))
                .ToArray();
        }
    }
}
