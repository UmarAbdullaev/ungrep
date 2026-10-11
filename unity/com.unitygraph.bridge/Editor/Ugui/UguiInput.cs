using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// The uGUI half of `ug ui`, `ug click` and `ug nav` (specs/SPEC-play-input.md). Elements are the Selectables and
    /// click handlers under a Canvas; a click is sent where a mouse would, through the EventSystem's
    /// raycast. TextMeshPro's input field, dropdown and text are reached by reflection, so the
    /// assembly does not depend on a package that may be absent (it is part of com.unity.ugui only
    /// from Unity 2023.2).
    /// </summary>
    [InitializeOnLoad]
    internal sealed partial class UguiInput : IUiInput
    {
        private static readonly Vector3[] _corners = new Vector3[4];

        static UguiInput()
        {
            PlayInput.Ui = new UguiInput();
        }

        private UguiInput()
        {
        }

        private sealed class Element
        {
            public GameObject Object;
            public string Address;
            public string Kind;
            public string Label;
            public string Detail;
            public Vector2 Center;
            public Vector2 Size;
            public string State;
            public bool LabelCut;
        }

        public bool HasSelection => EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null;

        /// <summary>
        /// The elements for `ug ui`, centre y down. In edit mode no EventSystem or raycaster is running,
        /// so nothing is checked for blocking; the headline then asks only whether an EventSystem exists.
        /// </summary>
        public void Gather(List<UiItem> into, int width, int height, out int canvases, out bool noEventSystem)
        {
            EventSystem events = EditorApplication.isPlaying ? EventSystem.current : null;
            canvases = UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Count(c => c.isRootCanvas && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded);
            noEventSystem = EditorApplication.isPlaying
                ? events == null
                : !UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Any(e => e.enabled && e.gameObject.scene.IsValid());
            foreach (Element element in Gather(width, height, events))
            {
                into.Add(new UiItem
                {
                    Address = element.Address,
                    Kind = element.Kind,
                    Detail = element.Detail,
                    Label = element.Label,
                    Name = element.Object.name,
                    Center = new Vector2(element.Center.x, height - element.Center.y),
                    Size = element.Size,
                    State = element.State,
                    LabelCut = element.LabelCut,
                });
            }
        }

        // The elements a player could meet, in reading order: top to bottom, then left to right.
        private static List<Element> Gather(int width, int height, EventSystem events)
        {
            var found = new List<Element>();
            var seen = new HashSet<GameObject>();
            foreach (Selectable selectable in UnityEngine.Object.FindObjectsByType<Selectable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (selectable.enabled && selectable.gameObject.activeInHierarchy && selectable.gameObject.scene.IsValid() && seen.Add(selectable.gameObject))
                {
                    Add(found, selectable.gameObject, selectable, width, height, events);
                }
            }

            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (behaviour is IPointerClickHandler && !(behaviour is Selectable) && behaviour.enabled && behaviour.gameObject.activeInHierarchy && behaviour.gameObject.scene.IsValid()
                    && behaviour.GetComponentInParent<Selectable>() == null && behaviour.GetComponentInParent<Canvas>() != null && seen.Add(behaviour.gameObject))
                {
                    Add(found, behaviour.gameObject, null, width, height, events);
                }
            }

            return found.OrderBy(e => Mathf.RoundToInt((height - e.Center.y) / 8f)).ThenBy(e => e.Center.x).ToList();
        }

        private static void Add(List<Element> found, GameObject go, Selectable selectable, int width, int height, EventSystem events)
        {
            if (!(go.transform is RectTransform rect) || !Bounds(rect, out Vector2 center, out Vector2 size))
            {
                return;
            }

            string kind = selectable == null ? "clickable" : KindOf(selectable);
            int cuts = UiItem.Cuts;
            string label = LabelOf(go);
            var element = new Element
            {
                Object = go,
                Address = Addresses.Of(go),
                Kind = kind,
                Label = label,
                Detail = Detail(kind, selectable, label, go),
                Center = center,
                Size = size,
            };
            element.LabelCut = UiItem.Cuts > cuts;

            if (selectable != null && !selectable.IsInteractable())
            {
                element.State = "disabled";
            }
            else if (center.x < 0 || center.y < 0 || center.x > width || center.y > height)
            {
                element.State = "offscreen";
            }
            else if (events != null)
            {
                if (!Hit(events, center, out RaycastResult hit))
                {
                    element.State = "no raycast target";
                }
                else if (!hit.gameObject.transform.IsChildOf(go.transform))
                {
                    element.State = "blocked by " + Addresses.Of(hit.gameObject);
                }
            }

            found.Add(element);
        }

        // The element's rectangle on screen: centre and size in pixels, Unity's origin (bottom left).
        private static bool Bounds(RectTransform rect, out Vector2 center, out Vector2 size)
        {
            center = Vector2.zero;
            size = Vector2.zero;
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            Canvas root = canvas == null ? null : canvas.rootCanvas;
            Camera camera = null;
            if (root != null && root.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                camera = root.worldCamera != null ? root.worldCamera : Camera.main;
                if (camera == null)
                {
                    return false;
                }
            }

            rect.GetWorldCorners(_corners);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);
            foreach (Vector3 corner in _corners)
            {
                Vector2 point = RectTransformUtility.WorldToScreenPoint(camera, corner);
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }

            center = (min + max) / 2;
            size = max - min;
            return true;
        }

        private static bool Hit(EventSystem events, Vector2 point, out RaycastResult hit)
        {
            var results = new List<RaycastResult>();
            events.RaycastAll(new PointerEventData(events) { position = point }, results);
            foreach (RaycastResult result in results)
            {
                if (result.gameObject != null)
                {
                    hit = result;
                    return true;
                }
            }

            hit = default;
            return false;
        }

        private static string KindOf(Selectable selectable)
        {
            if (selectable is Button)
            {
                return "button";
            }

            if (selectable is Toggle)
            {
                return "toggle";
            }

            if (selectable is Slider)
            {
                return "slider";
            }

            if (selectable is Scrollbar)
            {
                return "scrollbar";
            }

            if (selectable is InputField)
            {
                return "input";
            }

            if (selectable is Dropdown)
            {
                return "dropdown";
            }

            for (Type type = selectable.GetType(); type != null; type = type.BaseType)
            {
                if (type.Name == "TMP_InputField")
                {
                    return "input";
                }

                if (type.Name == "TMP_Dropdown")
                {
                    return "dropdown";
                }
            }

            return "selectable";
        }

        // What follows the kind on a line: the label, or the value for what holds one.
        private static string Detail(string kind, Selectable selectable, string label, GameObject go)
        {
            switch (kind)
            {
                case "toggle":
                    return Quote(label) + "  " + (((Toggle)selectable).isOn ? "on" : "off");
                case "slider":
                    var slider = (Slider)selectable;
                    return Number(slider.value, slider.wholeNumbers) + " (" + Number(slider.minValue, slider.wholeNumbers) + "-" + Number(slider.maxValue, slider.wholeNumbers) + ")";
                case "scrollbar":
                    return Number(((Scrollbar)selectable).value, false);
                case "input":
                    return Quote(Reflect.Get(selectable, "text") as string ?? "");
                case "dropdown":
                    return Quote(DropdownText(selectable)) + " (" + Answer.Count(DropdownOptions(selectable).Count, "option", "options") + ")";
                default:
                    return Quote(label.Length > 0 ? label : go.name);
            }
        }

        // The first text below the object (Text, or TextMeshPro through reflection), one line, cut for a line of output.
        private static string LabelOf(GameObject go)
        {
            foreach (Graphic graphic in go.GetComponentsInChildren<Graphic>(false))
            {
                string text = graphic is Text legacy ? legacy.text : Reflect.IsTmpText(graphic) ? Reflect.Get(graphic, "text") as string : null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return Cut(text);
                }
            }

            return "";
        }

        private static string Cut(string text) => UiItem.Cut(text);

        private static string Quote(string text) => UiItem.Quote(text);

        private static string Number(float value, bool whole) => UiItem.Number(value, whole);

        private static List<string> DropdownOptions(Selectable dropdown)
        {
            var texts = new List<string>();
            if (Reflect.Get(dropdown, "options") is IList options)
            {
                foreach (object option in options)
                {
                    texts.Add(Reflect.Get(option, "text") as string ?? "");
                }
            }

            return texts;
        }

        private static string DropdownText(Selectable dropdown)
        {
            List<string> texts = DropdownOptions(dropdown);
            return Reflect.Get(dropdown, "value") is int index && index >= 0 && index < texts.Count ? texts[index] : "";
        }

        /// <summary>Public members read and written by name, for the TextMeshPro types this assembly does not reference.</summary>
        private static class Reflect
        {
            private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance;

            public static bool IsTmpText(object target)
            {
                for (Type type = target.GetType(); type != null; type = type.BaseType)
                {
                    if (type.Name == "TMP_Text")
                    {
                        return true;
                    }
                }

                return false;
            }

            public static object Get(object target, string name)
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, Flags);
                if (property != null && property.CanRead)
                {
                    return property.GetValue(target);
                }

                return type.GetField(name, Flags)?.GetValue(target);
            }

            public static bool Set(object target, string name, object value)
            {
                PropertyInfo property = target.GetType().GetProperty(name, Flags);
                if (property == null || !property.CanWrite)
                {
                    return false;
                }

                property.SetValue(target, value);
                return true;
            }
        }
    }
}
