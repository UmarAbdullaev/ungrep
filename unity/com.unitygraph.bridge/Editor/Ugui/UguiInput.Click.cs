using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityGraph.Bridge
{
    /// <summary>`ug click`: a pointer click where a mouse would send it, or a value set through the component.</summary>
    internal sealed partial class UguiInput
    {
        private const int MaxNames = 5;

        public Reply Click(string element, string value)
        {
            EventSystem events = EventSystem.current;
            if (events == null)
            {
                return Answer.Error("no EventSystem in the loaded scenes: ug click needs one (add it in edit mode: GameObject > UI > Event System)");
            }

            if (!PlayInput.ScreenSize(out int width, out int height))
            {
                return Answer.Error("the Game view has no size (this editor runs without a window): ug click needs one");
            }

            GameObject target = null;
            Vector2 point;
            if (PlayInput.TryPoint(element, out float x, out float y))
            {
                if (value != null)
                {
                    return Answer.Error("--value operates an element's component: give an address or label, not a point");
                }

                point = new Vector2(x, height - y);
            }
            else
            {
                target = Resolve(element, width, height, events, out string error);
                if (target == null)
                {
                    return Answer.Error(error);
                }

                if (!(target.transform is RectTransform rect) || !Bounds(rect, out point, out _))
                {
                    return Answer.Error(Addresses.Of(target) + " is not on a screen canvas: nothing to click");
                }
            }

            if (value != null)
            {
                return SetValue(target, value);
            }

            if (point.x < 0 || point.y < 0 || point.x > width || point.y > height)
            {
                return Answer.Error((target == null ? "the point" : Addresses.Of(target)) + " is off screen at " + Where(point, height) + " (the Game view is " + width + "x" + height + ")");
            }

            return Press(events, target, point, height);
        }

        // An address, else a label (or object name) from the elements on screen.
        private static GameObject Resolve(string element, int width, int height, EventSystem events, out string error)
        {
            GameObject found = Addresses.Find(element, out string addressError);
            if (found != null)
            {
                error = null;
                return found;
            }

            List<Element> all = Gather(width, height, events);
            List<Element> matches = all.Where(e => Same(e, element)).ToList();
            if (matches.Count == 0)
            {
                matches = all.Where(e => e.Label.IndexOf(element, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            }

            if (matches.Count == 1)
            {
                error = null;
                return matches[0].Object;
            }

            error = matches.Count == 0
                ? addressError + " (ug ui lists the elements)"
                : "ambiguous: \"" + element + "\" (" + string.Join(", ", matches.Take(MaxNames).Select(e => e.Address)) + (matches.Count > MaxNames ? ", ..." : "") + ")";
            return null;
        }

        private static bool Same(Element e, string text) =>
            string.Equals(e.Label, text, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Object.name, text, StringComparison.OrdinalIgnoreCase);

        // Enter, down, up and click on what the raycast finds at the point, as the StandaloneInputModule sends them.
        private static Reply Press(EventSystem events, GameObject target, Vector2 point, int height)
        {
            string where = Where(point, height);
            if (!Hit(events, point, out RaycastResult hit))
            {
                return Answer.Error(target == null
                    ? "nothing at " + where + " receives a click"
                    : Addresses.Of(target) + " has no raycast target: nothing at " + where + " receives a click (a Graphic with Raycast Target on)");
            }

            GameObject hitObject = hit.gameObject;
            if (target != null && !hitObject.transform.IsChildOf(target.transform))
            {
                return Answer.Error("blocked: " + Addresses.Of(hitObject) + " is on top of " + Addresses.Of(target) + " at " + where);
            }

            var data = new PointerEventData(events)
            {
                position = point,
                pressPosition = point,
                button = PointerEventData.InputButton.Left,
                clickCount = 1,
                clickTime = Time.unscaledTime,
                pointerCurrentRaycast = hit,
                pointerPressRaycast = hit,
                eligibleForClick = true,
                pointerEnter = hitObject,
                rawPointerPress = hitObject,
            };

            ExecuteEvents.ExecuteHierarchy(hitObject, data, ExecuteEvents.pointerEnterHandler);
            GameObject clicked = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hitObject);
            data.pointerPress = ExecuteEvents.ExecuteHierarchy(hitObject, data, ExecuteEvents.pointerDownHandler) ?? clicked;
            events.SetSelectedGameObject(ExecuteEvents.GetEventHandler<ISelectHandler>(hitObject), data);
            if (data.pointerPress != null)
            {
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
            }

            if (clicked != null && data.pointerPress == clicked)
            {
                ExecuteEvents.Execute(clicked, data, ExecuteEvents.pointerClickHandler);
            }

            GameObject receiver = clicked != null ? clicked : hitObject;
            Selectable selectable = receiver.GetComponentInParent<Selectable>();
            string what = Addresses.Of(selectable != null ? selectable.gameObject : receiver);
            if (selectable != null)
            {
                what += " (" + KindOf(selectable) + " " + Quote(LabelOf(selectable.gameObject)) + ")";
            }
            else if (clicked == null)
            {
                what += " (nothing there handles clicks)";
            }

            return Answer.Line("clicked " + what + " at " + where);
        }

        // The component's own API, for what a click cannot set. Refused for what a player could not use.
        private static Reply SetValue(GameObject target, string value)
        {
            Selectable selectable = target.GetComponentInParent<Selectable>();
            if (selectable == null)
            {
                return Answer.Error(Addresses.Of(target) + " has no slider, scrollbar, input, dropdown or toggle: --value cannot set it");
            }

            string address = Addresses.Of(selectable.gameObject);
            string kind = KindOf(selectable);
            if (!selectable.IsInteractable())
            {
                return Answer.Error(address + " is not interactable: a player could not set it");
            }

            string set = "set " + address + " (" + kind + ") ";
            switch (kind)
            {
                case "slider":
                case "scrollbar":
                    return Numeric(selectable, kind, address, value, set);
                case "toggle":
                    {
                        string text = value.Trim().ToLowerInvariant();
                        if (text != "on" && text != "off" && text != "true" && text != "false")
                        {
                            return Answer.Error("a toggle takes on or off, not " + value);
                        }

                        var toggle = (Toggle)selectable;
                        bool before = toggle.isOn;
                        toggle.isOn = text == "on" || text == "true";
                        return Answer.Line(set + (before ? "on" : "off") + " -> " + (toggle.isOn ? "on" : "off"));
                    }

                case "input":
                    {
                        string before = Reflect.Get(selectable, "text") as string ?? "";
                        Reflect.Set(selectable, "text", value);
                        foreach (string name in new[] { "onEndEdit", "onSubmit" })
                        {
                            (Reflect.Get(selectable, name) as UnityEvent<string>)?.Invoke(value);
                        }

                        return Answer.Line(set + Quote(before) + " -> " + Quote(Reflect.Get(selectable, "text") as string ?? ""));
                    }

                case "dropdown":
                    return Choose(selectable, value, set);
                default:
                    return Answer.Error(address + " is a " + kind + ": --value takes a slider, scrollbar, input, dropdown or toggle");
            }
        }

        private static Reply Numeric(Selectable selectable, string kind, string address, string value, string set)
        {
            float min = kind == "slider" ? ((Slider)selectable).minValue : 0f;
            float max = kind == "slider" ? ((Slider)selectable).maxValue : 1f;
            bool whole = kind == "slider" && ((Slider)selectable).wholeNumbers;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
            {
                return Answer.Error(address + " takes a number from " + Number(min, whole) + " to " + Number(max, whole) + ", not " + value);
            }

            if (number < min || number > max)
            {
                return Answer.Error(Number(number, whole) + " is outside " + address + "'s range " + Number(min, whole) + "-" + Number(max, whole));
            }

            if (kind == "slider")
            {
                var slider = (Slider)selectable;
                float before = slider.value;
                slider.value = number;
                return Answer.Line(set + Number(before, whole) + " -> " + Number(slider.value, whole));
            }

            var scrollbar = (Scrollbar)selectable;
            float was = scrollbar.value;
            scrollbar.value = number;
            return Answer.Line(set + Number(was, false) + " -> " + Number(scrollbar.value, false));
        }

        // A dropdown option by index or by its text.
        private static Reply Choose(Selectable dropdown, string value, string set)
        {
            List<string> options = DropdownOptions(dropdown);
            int index = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : options.FindIndex(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index >= options.Count)
            {
                return Answer.Error("no option " + value + " (options: " + string.Join(", ", options.Take(MaxNames)) + (options.Count > MaxNames ? ", ..." : "") + ")");
            }

            string before = DropdownText(dropdown);
            Reflect.Set(dropdown, "value", index);
            return Answer.Line(set + Quote(before) + " -> " + Quote(DropdownText(dropdown)));
        }

        // `640,360`: a point in Game view pixels, y down.
        private static string Where(Vector2 point, int height) =>
            Mathf.RoundToInt(point.x).ToString(CultureInfo.InvariantCulture) + "," + Mathf.RoundToInt(height - point.y).ToString(CultureInfo.InvariantCulture);
    }
}
