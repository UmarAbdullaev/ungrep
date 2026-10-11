using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `ug click`, `--value` and `ug nav` on UI Toolkit elements: pointer and navigation events sent
    /// through the element's panel, which runs the game's handlers as for a real mouse or gamepad
    /// (SPEC-play-input, UI Toolkit, spike 2). No EventSystem or input device is involved.
    /// </summary>
    internal static partial class UiToolkit
    {
        private const int MaxOptions = 5;

        /// <summary>A click at the element's centre, refused when something else is on top there.</summary>
        public static Reply Click(Target target, string value)
        {
            if (value != null)
            {
                return SetValue(target, value);
            }

            if (!target.Document.OnScreen)
            {
                return Answer.Error(target.Address + " is in a world-space or render-texture UI Toolkit document: ug click reaches the screen panels (ug nav and --value work)");
            }

            PlayInput.ScreenSize(out int width, out int height);
            Vector2 point = target.Document.ToScreen(target.Element.worldBound.center);
            string where = Where(point);
            if (float.IsNaN(point.x) || point.x < 0 || point.y < 0 || point.x > width || point.y > height)
            {
                return Answer.Error(target.Address + " is off screen at " + where + " (the Game view is " + width + "x" + height + ")");
            }

            List<Document> documents = Documents(out _, out string problem);
            if (problem != null)
            {
                return Answer.Error(problem);
            }

            Target top = Pick(documents, point);
            if (top == null)
            {
                return Answer.Error(target.Address + " has no pick target: nothing at " + where + " receives a click (picking-mode: ignore)");
            }

            if (top.Element != target.Element && !target.Element.Contains(top.Element))
            {
                return Answer.Error("blocked: " + top.Address + " is on top of " + target.Address + " at " + where);
            }

            Press(top.Document, point);
            return Answer.Line("clicked " + Described(target.Document, target.Element) + " at " + where);
        }

        /// <summary>A click at a Game view point when a UI Toolkit element is the top pick there; null when none is.</summary>
        public static Reply ClickPoint(Vector2 point)
        {
            List<Document> documents = Documents(out _, out string problem);
            Target top = problem == null ? Pick(documents, point) : null;
            if (top == null)
            {
                return null;
            }

            Press(top.Document, point);
            VisualElement listed = top.Element;
            string what = top.Address;
            if (documents.Any(d => d.Root == top.Document.Root && (d.Root == top.Element || d.Root.Contains(top.Element))))
            {
                listed = Listed(top.Document, top.Element);
                what = Described(top.Document, listed);
            }

            if (KindOf(listed) == null && !Handles(listed))
            {
                what += " (nothing there handles clicks)";
            }

            return Answer.Line("clicked " + what + " at " + Where(point));
        }

        // Move, down and up at the point, made from IMGUI events as the runtime panel's own input does.
        private static void Press(Document document, Vector2 screen)
        {
            Vector2 at = document.ToPanel(screen);
            VisualElement tree = document.Panel.visualTree;
            using (PointerMoveEvent move = PointerMoveEvent.GetPooled(Mouse(EventType.MouseMove, at)))
            {
                tree.SendEvent(move);
            }

            using (PointerDownEvent down = PointerDownEvent.GetPooled(Mouse(EventType.MouseDown, at)))
            {
                tree.SendEvent(down);
            }

            using (PointerUpEvent up = PointerUpEvent.GetPooled(Mouse(EventType.MouseUp, at)))
            {
                tree.SendEvent(up);
            }
        }

        private static Event Mouse(EventType type, Vector2 at) => new Event { type = type, mousePosition = at, button = 0, clickCount = 1 };

        // Whether it or something above it has event callbacks.
        private static bool Handles(VisualElement element)
        {
            for (VisualElement e = element; e != null; e = e.hierarchy.parent)
            {
                if (e.HasBubbleUpHandlers() || e.HasTrickleDownHandlers())
                {
                    return true;
                }
            }

            return false;
        }

        // `Menu/Play (button "Play")`, as the uGUI answers name an element.
        private static string Described(Document document, VisualElement element)
        {
            string kind = KindOf(element) ?? (element.HasBubbleUpHandlers() || element.HasTrickleDownHandlers() ? "clickable" : null);
            string address = AddressOf(document, element);
            return kind == null ? address : address + " (" + kind + " " + UiItem.Quote(LabelOf(element)) + ")";
        }

        /// <summary>`--value` and `ug set <element> value`: the control's value through its own setter, so `ChangeEvent` callbacks run.</summary>
        public static Reply SetValue(Target target, string value)
        {
            VisualElement element = target.Element;
            string kind = KindOf(element);
            if (kind == null || kind == "button")
            {
                return Answer.Error(target.Address + " is a " + (kind ?? element.GetType().Name) + ": --value takes a toggle, radio, foldout, slider, scrollbar, input, dropdown or field");
            }

            if (!element.enabledInHierarchy)
            {
                return Answer.Error(target.Address + " is disabled: a player could not set it");
            }

            string before = ValueText(element);
            string error = Apply(element, value.Trim(), value);
            return error != null
                ? Answer.Error(target.Address + " " + error)
                : Answer.Line("set " + target.Address + " (" + kind + ") " + before + " -> " + ValueText(element));
        }

        // Null when the value was set, else what is wrong with it (after the address).
        private static string Apply(VisualElement element, string text, string raw)
        {
            switch (element)
            {
                case Foldout foldout:
                    {
                        bool? open = Switch(text, "open", "closed");
                        if (open == null)
                        {
                            return "takes open or closed (on, off), not " + text;
                        }

                        foldout.value = open.Value;
                        return null;
                    }

                case RadioButtonGroup group:
                    {
                        List<string> choices = group.choices?.ToList() ?? new List<string>();
                        int index = Option(choices, text);
                        if (index < 0)
                        {
                            return "has no option " + text + " (options: " + Options(choices) + ")";
                        }

                        group.value = index;
                        return null;
                    }

                case BaseField<bool> toggle:
                    {
                        bool? on = Switch(text, "on", "off");
                        if (on == null)
                        {
                            return "takes on or off, not " + text;
                        }

                        toggle.value = on.Value;
                        return null;
                    }

                case Scroller scroller:
                    return Number(text, scroller.lowValue, scroller.highValue, false, n => scroller.value = n);
                case BaseSlider<float> slider:
                    return Number(text, slider.lowValue, slider.highValue, false, n => slider.value = n);
                case BaseSlider<int> whole:
                    return Number(text, whole.lowValue, whole.highValue, true, n => whole.value = Mathf.RoundToInt(n));
                case MinMaxSlider range:
                    {
                        string[] parts = text.Split(',');
                        if (parts.Length != 2
                            || !float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float min)
                            || !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float max)
                            || min > max || min < range.lowLimit || max > range.highLimit)
                        {
                            return "takes min,max within " + UiItem.Number(range.lowLimit, false) + "-" + UiItem.Number(range.highLimit, false) + ", not " + text;
                        }

                        range.value = new Vector2(min, max);
                        return null;
                    }

                case TextInputBaseField<string> input:
                    input.value = raw;
                    return null;
                case BasePopupField<string, string> popup:
                    {
                        List<string> choices = popup.choices ?? new List<string>();
                        int index = Option(choices, text);
                        if (index < 0)
                        {
                            return "has no option " + text + " (options: " + Options(choices) + ")";
                        }

                        popup.value = choices[index];
                        return null;
                    }

                case EnumField choice:
                    {
                        Type type = choice.value?.GetType();
                        if (type == null)
                        {
                            return "has no enum type yet (EnumField.Init was not called)";
                        }

                        string[] names = Enum.GetNames(type);
                        string name = names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                        if (name == null)
                        {
                            return "has no option " + text + " (options: " + Options(names) + ")";
                        }

                        choice.value = (Enum)Enum.Parse(type, name);
                        return null;
                    }
            }

            Type valueType = FieldType(element.GetType());
            PropertyInfo property = element.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
            if (valueType == null || property == null || !property.CanWrite)
            {
                return "has no value ug can set";
            }

            string problem = Values.ParseAs(valueType, raw, out object parsed, out _);
            if (problem != null)
            {
                return "takes a " + valueType.Name + ": " + problem;
            }

            property.SetValue(element, parsed);
            return null;
        }

        private static bool? Switch(string text, string yes, string no)
        {
            string t = text.ToLowerInvariant();
            if (t == yes || t == "on" || t == "true")
            {
                return true;
            }

            return t == no || t == "off" || t == "false" ? false : (bool?)null;
        }

        private static string Number(string text, float low, float high, bool whole, Action<float> set)
        {
            float min = Mathf.Min(low, high);
            float max = Mathf.Max(low, high);
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || float.IsNaN(number))
            {
                return "takes a number from " + UiItem.Number(min, whole) + " to " + UiItem.Number(max, whole) + ", not " + text;
            }

            if (number < min || number > max)
            {
                return "takes " + UiItem.Number(min, whole) + "-" + UiItem.Number(max, whole) + ", not " + text;
            }

            set(number);
            return null;
        }

        // An option by its text (any case) or its index.
        private static int Option(IList<string> choices, string text)
        {
            for (int i = 0; i < choices.Count; i++)
            {
                if (string.Equals(choices[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < choices.Count ? index : -1;
        }

        private static string Options(IEnumerable<string> choices)
        {
            List<string> all = choices.ToList();
            return string.Join(", ", all.Take(MaxOptions)) + (all.Count > MaxOptions ? ", ..." : "");
        }

        // The T of the BaseField<T> the type derives from, or null.
        private static Type FieldType(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BaseField<>))
                {
                    return t.GetGenericArguments()[0];
                }
            }

            return null;
        }

        /// <summary>The value of a value control as `ug` prints it: `on`, `0.8`, `"High"`, `open`; empty for others.</summary>
        public static string ValueText(VisualElement element)
        {
            switch (element)
            {
                case Foldout foldout:
                    return foldout.value ? "open" : "closed";
                case RadioButtonGroup group:
                    {
                        List<string> choices = group.choices?.ToList() ?? new List<string>();
                        return UiItem.Quote(group.value >= 0 && group.value < choices.Count ? choices[group.value] : "");
                    }

                case BaseField<bool> toggle:
                    return toggle.value ? "on" : "off";
                case Scroller scroller:
                    return UiItem.Number(scroller.value, false);
                case BaseSlider<float> slider:
                    return UiItem.Number(slider.value, false);
                case BaseSlider<int> whole:
                    return whole.value.ToString(CultureInfo.InvariantCulture);
                case MinMaxSlider range:
                    return UiItem.Number(range.minValue, false) + "," + UiItem.Number(range.maxValue, false);
                case TextInputBaseField<string> input:
                    return UiItem.Quote(input.value);
                case BasePopupField<string, string> popup:
                    return UiItem.Quote(popup.value);
                case EnumField choice:
                    return UiItem.Quote(choice.value?.ToString() ?? "");
            }

            if (FieldType(element.GetType()) == null)
            {
                return "";
            }

            object value = element.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(element);
            return UiItem.Cut(Values.Show(value, Values.Caps.Compact, out _));
        }

        /// <summary>The focused element of the top panel that has one, or null.</summary>
        public static Target Focused()
        {
            List<Document> documents = Documents(out _, out string problem);
            if (problem != null)
            {
                return null;
            }

            foreach (Document document in documents)
            {
                if (document.Panel.focusController?.focusedElement is VisualElement focused && (focused == document.Root || document.Root.Contains(focused))
                    && !document.Nested.Any(n => n.Contains(focused)))
                {
                    return Of(document, focused);
                }
            }

            return null;
        }

        /// <summary>`ug nav`: a navigation event to the focused element, after focusing <paramref name="target"/> when asked.</summary>
        public static Reply Nav(string direction, Target target, bool focusFirst)
        {
            VisualElement element = target.Element;
            if (focusFirst)
            {
                if (!element.canGrabFocus)
                {
                    return Answer.Error(target.Address + " cannot take the focus (not focusable, disabled or hidden)");
                }

                element.Focus();
            }

            IPanel panel = target.Document.Panel;
            string before = target.Address;
            EventBase sent;
            switch (direction)
            {
                case "submit":
                    sent = NavigationSubmitEvent.GetPooled();
                    break;
                case "cancel":
                    sent = NavigationCancelEvent.GetPooled();
                    break;
                case "next":
                case "previous":
#if UNITY_6000_0_OR_NEWER
                    sent = NavigationMoveEvent.GetPooled(direction == "next" ? NavigationMoveEvent.Direction.Next : NavigationMoveEvent.Direction.Previous);
                    break;
#else
                    return Answer.Error("this Unity has no next/previous navigation event (Unity 6 and later): use up, down, left or right");
#endif
                default:
                    sent = NavigationMoveEvent.GetPooled(Move(direction));
                    break;
            }

            using (sent)
            {
                panel.visualTree.SendEvent(sent);
            }

            if (direction == "submit" || direction == "cancel")
            {
                return Answer.Line("nav " + direction + " on " + Described(target.Document, element));
            }

            Target after = Focused();
            if (after == null && panel.focusController?.focusedElement is VisualElement inside && target.Document.Root.Contains(inside))
            {
                after = Of(target.Document, inside);
            }

            string now = after == null ? "nothing" : after.Address;
            string value = KindOf(element) == null ? "" : ValueText(element);
            string label = LabelOf(element);
            value = string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(label) ? UiItem.Quote(label) : value;
            string change = after != null && after.Element == element ? now + " (focus kept" + (string.IsNullOrEmpty(value) ? ")" : ", " + value + ")") : now;
            return Answer.Line("nav " + direction + ": " + before + " -> " + change);
        }

        private static Vector2 Move(string direction)
        {
            switch (direction)
            {
                case "up":
                    return Vector2.up;
                case "down":
                    return Vector2.down;
                case "left":
                    return Vector2.left;
                default:
                    return Vector2.right;
            }
        }

        // `640,360`: a Game view point, y down.
        private static string Where(Vector2 point) =>
            Mathf.RoundToInt(point.x).ToString(CultureInfo.InvariantCulture) + "," + Mathf.RoundToInt(point.y).ToString(CultureInfo.InvariantCulture);
    }
}
