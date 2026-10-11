using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>The uGUI side of play-input, set by the Ugui assembly when com.unity.ugui is present.</summary>
    internal interface IUiInput
    {
        /// <summary>The uGUI elements a player could meet, in Game view pixels (y down), and what the `ug ui` headline says about them.</summary>
        void Gather(List<UiItem> into, int width, int height, out int canvases, out bool noEventSystem);

        /// <summary>`ug click`: an element address or label, or `x,y` in Game view pixels (y down); a value operates the component.</summary>
        Reply Click(string element, string value);

        /// <summary>`ug nav`: a navigation event on the selected object, after selecting <paramref name="element"/> when given.</summary>
        Reply Nav(string direction, string element);

        /// <summary>Whether the EventSystem has a selected object for `ug nav` without an element.</summary>
        bool HasSelection { get; }
    }

    /// <summary>One `ug ui` line: a uGUI or UI Toolkit element, its place in Game view pixels (y down) and what stops a player using it.</summary>
    internal sealed class UiItem
    {
        public string Address;
        public string Kind;
        public string Detail;
        public string Classes;
        public string Label;
        public string Name;
        public Vector2 Center;
        public Vector2 Size;
        public string State;
        public bool Toolkit;

        /// <summary>Whether <see cref="Cut"/> shortened this element's label or value.</summary>
        public bool LabelCut;

        public string Line(bool withState) =>
            Address + "  " + Kind + "  " + Detail + (string.IsNullOrEmpty(Classes) ? "" : "  " + Classes) + "  "
            + Mathf.RoundToInt(Center.x).ToString(CultureInfo.InvariantCulture) + "," + Mathf.RoundToInt(Center.y).ToString(CultureInfo.InvariantCulture) + "  "
            + Mathf.RoundToInt(Size.x).ToString(CultureInfo.InvariantCulture) + "x" + Mathf.RoundToInt(Size.y).ToString(CultureInfo.InvariantCulture)
            + (withState && State != null ? "  " + State : "");

        private const int LabelLength = 30;

        /// <summary>How many texts <see cref="Cut"/> has shortened: `ug ui` and `ug tree --ui` compare it to name `ug inspect`.</summary>
        public static int Cuts;

        /// <summary>The last line of an answer with a cut label.</summary>
        public const string CutNote = "(labels cut at 30 characters: ug inspect <element>)";

        /// <summary>One line of text, cut for a line of output.</summary>
        public static string Cut(string text)
        {
            text = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length <= LabelLength)
            {
                return text;
            }

            Cuts++;
            return text.Substring(0, LabelLength - 3) + "...";
        }

        public static string Quote(string text) => "\"" + Cut(text) + "\"";

        /// <summary>The whole text in quotes, line breaks as `\n`: `ug inspect`.</summary>
        public static string Whole(string text) => "\"" + (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n") + "\"";

        public static string Number(float value, bool whole) =>
            whole ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>`ug ui`, `ug click` and `ug nav` over uGUI and UI Toolkit (SPEC-play-input, UI Toolkit).</summary>
    internal static partial class PlayInput
    {
        private const int DefaultLines = 40;
        private const int AllLines = 150;
        private const int MaxNames = 5;

        private static readonly string[] _directions = { "submit", "cancel", "up", "down", "left", "right", "next", "previous" };

        /// <summary>`/ui`: reads, so edit mode too.</summary>
        public static Reply UiList(Request request) =>
            List(request.Get("all") == "true", int.TryParse(request.Get("from"), NumberStyles.None, CultureInfo.InvariantCulture, out int from) ? from : 0);

        public static Func<Reply> UiClick(Request request)
        {
            string element = request.Get("element");
            Reply refused = NeedPlaying(false);
            if (refused == null && string.IsNullOrWhiteSpace(element))
            {
                refused = Answer.Error("ug click needs an element: an address or label from ug ui, or x,y");
            }

            if (refused != null)
            {
                return () => refused;
            }

            string value = request.Get("value");
            return InputQueue.Start(new PlayAction("click") { Start = () => Click(element.Trim(), value) });
        }

        public static Func<Reply> UiNav(Request request)
        {
            string direction = (request.Get("direction") ?? "").Trim().ToLowerInvariant();
            string element = request.Get("element");
            Reply refused = Array.IndexOf(_directions, direction) < 0
                ? Answer.Error("ug nav takes " + string.Join(", ", _directions) + (direction.Length == 0 ? "" : ", not " + direction))
                : NeedPlaying(false);

            if (refused != null)
            {
                return () => refused;
            }

            return InputQueue.Start(new PlayAction("nav " + direction) { Start = () => Nav(direction, string.IsNullOrWhiteSpace(element) ? null : element.Trim()) });
        }

        /// <summary>The `ug ui` lines: uGUI and UI Toolkit together in reading order.</summary>
        internal static Reply List(bool all, int from = 0)
        {
            if (!ScreenSize(out int width, out int height))
            {
                return Answer.Error("the Game view has no size (this editor runs without a window): ug ui needs one");
            }

            var items = new List<UiItem>();
            int canvases = 0;
            bool noEvents = false;
            Ui?.Gather(items, width, height, out canvases, out noEvents);
            UiToolkit.Census census = UiToolkit.Gather(items, width, height, out string problem);
            if (problem != null)
            {
                return Answer.Error(problem);
            }

            items = items.OrderBy(e => Mathf.RoundToInt(e.Center.y / 8f)).ThenBy(e => e.Center.x).ToList();
            var lines = new List<string> { Head(width, height, canvases, noEvents, census) };
            List<UiItem> shown = all ? items : items.Where(e => e.State == null).ToList();
            int limit = all ? AllLines : DefaultLines;
            if (from > 0 && from >= shown.Count)
            {
                lines.Add("  (nothing from element " + from.ToString(CultureInfo.InvariantCulture) + ": " + Answer.Count(shown.Count, "element", "elements") + ")");
                return Answer.Lines(lines);
            }

            List<UiItem> page = shown.Skip(from).Take(limit).ToList();
            foreach (UiItem item in page)
            {
                lines.Add(item.Line(all));
            }

            var more = new List<string>();
            if (!all && items.Count > shown.Count)
            {
                var states = items.Where(e => e.State != null)
                    .GroupBy(e => e.State.StartsWith("blocked", StringComparison.Ordinal) ? "blocked" : e.State)
                    .Select(g => g.Count().ToString(CultureInfo.InvariantCulture) + " " + g.Key);
                more.Add(Answer.Count(items.Count - shown.Count, "more", "more") + " not usable (" + string.Join(", ", states) + "): ug ui --all");
            }

            int left = shown.Count - from - page.Count;
            if (left > 0)
            {
                more.Add("(+" + left.ToString(CultureInfo.InvariantCulture) + " more: ug ui --all" + (all ? " --from " + (from + page.Count).ToString(CultureInfo.InvariantCulture) : "") + ")");
            }

            if (more.Count > 0)
            {
                lines.Add("  " + string.Join("; ", more));
            }

            if (page.Any(e => e.LabelCut))
            {
                lines.Add("  " + UiItem.CutNote);
            }

            return Answer.Lines(lines);
        }

        private static string Head(int width, int height, int canvases, bool noEvents, UiToolkit.Census census)
        {
            string state = !EditorApplication.isPlaying ? "edit mode" : EditorApplication.isPaused ? "paused" : "playing";
            var parts = new List<string>();
            if (canvases > 0 || (Ui != null && census.Documents == 0))
            {
                parts.Add(canvases == 0 ? "no canvas" : Answer.Count(canvases, "canvas", "canvases"));
            }

            if (census.Documents > 0)
            {
                parts.Add(Answer.Count(census.Documents, "UI Toolkit document", "UI Toolkit documents"));
            }

            if (parts.Count == 0)
            {
                parts.Add("no UI Toolkit document (com.unity.ugui is not in this project)");
            }

            if (census.Elsewhere > 0)
            {
                parts.Add(Answer.Count(census.Elsewhere, "world-space or render-texture UI Toolkit document", "world-space or render-texture UI Toolkit documents") + " not listed");
            }

            if (census.Unattached > 0)
            {
                parts.Add(Answer.Count(census.Unattached, "UI Toolkit document", "UI Toolkit documents") + " not shown (no PanelSettings or UXML)");
            }

            string head = "ui  " + width + "x" + height + "  " + state + "  " + string.Join(", ", parts);
            if (Ui != null && noEvents && (canvases > 0 || census.Documents == 0))
            {
                head += census.Documents > 0 ? "  no EventSystem (uGUI receives no clicks)" : "  no EventSystem (nothing receives clicks)";
            }

            return head;
        }

        /// <summary>`ug click` after the play check: a point picks UI Toolkit first, then the uGUI raycast.</summary>
        internal static Reply Click(string element, string value)
        {
            if (!ScreenSize(out int width, out int height))
            {
                return Answer.Error("the Game view has no size (this editor runs without a window): ug click needs one");
            }

            if (TryPoint(element, out float x, out float y))
            {
                if (value != null)
                {
                    return Answer.Error("--value operates an element's component: give an address or label, not a point");
                }

                Reply picked = UiToolkit.ClickPoint(new Vector2(x, y));
                if (picked != null)
                {
                    return picked;
                }

                return Ui != null
                    ? Ui.Click(element, null)
                    : Answer.Error("nothing at " + Mathf.RoundToInt(x) + "," + Mathf.RoundToInt(y) + " receives a click");
            }

            return Route(element, width, height, "click", address => Ui.Click(address, value), target => UiToolkit.Click(target, value));
        }

        /// <summary>`ug nav` after the play and direction checks.</summary>
        internal static Reply Nav(string direction, string element)
        {
            if (element != null)
            {
                if (!ScreenSize(out int width, out int height))
                {
                    return Answer.Error("the Game view has no size (this editor runs without a window): ug nav needs one");
                }

                if (TryPoint(element, out _, out _))
                {
                    return Answer.Error("ug nav takes an element address or label, not a point");
                }

                return Route(element, width, height, "nav", address => Ui.Nav(direction, address), target => UiToolkit.Nav(direction, target, true));
            }

            UiToolkit.Target focused = UiToolkit.Focused();
            if (focused != null)
            {
                return UiToolkit.Nav(direction, focused, false);
            }

            if (Ui != null && Ui.HasSelection)
            {
                return Ui.Nav(direction, null);
            }

            return Answer.Error("nothing has the focus: give an element (ug nav " + direction + " Menu/Play)");
        }

        // An address or label to the system that has it: a game object is uGUI, an element address UI Toolkit,
        // a label whichever element of the two lists has it (exact first, then a part).
        private static Reply Route(string element, int width, int height, string command, Func<string, Reply> ugui, Func<UiToolkit.Target, Reply> toolkit)
        {
            GameObject found = Addresses.Find(element, out string addressError);
            if (found != null)
            {
                if (found.GetComponent<UnityEngine.UIElements.UIDocument>() != null)
                {
                    return Answer.Error(Addresses.Of(found) + " is a UI Toolkit document: give one of its elements (ug ui lists them)");
                }

                return Ui == null ? NoUgui(command) : ugui(element);
            }

            UiToolkit.Target target = UiToolkit.Find(element, out string toolkitError);
            if (target != null)
            {
                return toolkit(target);
            }

            if (toolkitError != null)
            {
                return Answer.Error(toolkitError);
            }

            var items = new List<UiItem>();
            Ui?.Gather(items, width, height, out _, out _);
            UiToolkit.Gather(items, width, height, out _);
            List<UiItem> matches = items.Where(e => string.Equals(e.Label, element, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Name, element, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                matches = items.Where(e => (e.Label ?? "").IndexOf(element, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            if (matches.Count == 1)
            {
                return matches[0].Toolkit ? toolkit(UiToolkit.Find(matches[0].Address, out _)) : ugui(matches[0].Address);
            }

            return Answer.Error(matches.Count == 0
                ? addressError + " (ug ui lists the elements)"
                : "ambiguous: \"" + element + "\" (" + string.Join(", ", matches.Take(MaxNames).Select(e => e.Address))
                    + (matches.Count > MaxNames ? ", ... " + (matches.Count - MaxNames).ToString(CultureInfo.InvariantCulture) + " more: ug ui --all" : "") + ")");
        }

        private static Reply NoUgui(string command) =>
            Answer.Error("com.unity.ugui is not in this project: ug " + command + " on a game object needs it (UI Toolkit elements work without it)");
    }
}
