using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// UI Toolkit runtime documents (SPEC-play-input, UI Toolkit): the documents on screen, element
    /// addresses (`Menu/Play`, `Menu/Button#3`, `Menu/Pause/.primary`), the `ug ui` items, and the
    /// map between panel pixels and Game view pixels (y down). In edit mode nothing lays the panels
    /// out, so the reads call Unity's internal `UIElementsRuntimeUtility.UpdatePanels` first.
    /// </summary>
    internal static partial class UiToolkit
    {
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int MaxCandidates = 10;

        private static readonly Type _runtime = typeof(PanelSettings).Assembly.GetType("UnityEngine.UIElements.UIElementsRuntimeUtility");

        /// <summary>What `ug ui` counts in its headline.</summary>
        internal struct Census
        {
            public int Documents;
            public int Elsewhere;
            public int Unattached;
        }

        /// <summary>A document on a screen panel, with the map from its panel's pixels to the Game view's.</summary>
        internal sealed class Document
        {
            public UIDocument Component;
            public VisualElement Root;
            public IPanel Panel;
            public string Address;
            public float PanelOrder;
            public float Order;
            public HashSet<VisualElement> Nested;

            // False for a world-space or render-texture panel: its elements have no Game view place.
            public bool OnScreen = true;

            // Game view pixels per panel pixel, and the panel point at the Game view's top-left corner.
            public Vector2 Scale = Vector2.one;
            public Vector2 Origin;

            public Vector2 ToScreen(Vector2 point) => new Vector2((point.x - Origin.x) * Scale.x, (point.y - Origin.y) * Scale.y);

            public Vector2 ToPanel(Vector2 screen) => new Vector2(screen.x / Scale.x + Origin.x, screen.y / Scale.y + Origin.y);
        }

        /// <summary>An element and the document it is in, with its shortest address.</summary>
        internal sealed class Target
        {
            public Document Document;
            public VisualElement Element;
            public string Address;
        }

        /// <summary>Lays the runtime panels out when no frame does it (edit mode, paused); null, or why it cannot.</summary>
        public static string Layout()
        {
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                return null;
            }

            return Call("UpdatePanels");
        }

        // An internal UIElementsRuntimeUtility method without arguments, or one bool; null, or why it is missing.
        internal static string Call(string method, params object[] arguments)
        {
            Type[] types = arguments.Select(a => a.GetType()).ToArray();
            MethodInfo found = _runtime?.GetMethod(method, AnyStatic, null, types, null);
            if (found == null)
            {
                return "this Unity has no UIElementsRuntimeUtility." + method + ": ug cannot lay out or draw UI Toolkit here";
            }

            found.Invoke(null, arguments);
            return null;
        }

        /// <summary>The documents on screen panels, top panel first; the census counts the rest.</summary>
        public static List<Document> Documents(out Census census, out string problem)
        {
            census = default;
            problem = Layout();
            var documents = new List<Document>();
            if (problem != null)
            {
                return documents;
            }

            bool sized = PlayInput.ScreenSize(out int width, out int height);
            foreach (UIDocument component in UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!component.isActiveAndEnabled || !component.gameObject.scene.IsValid())
                {
                    continue;
                }

                VisualElement root = component.rootVisualElement;
                PanelSettings settings = SettingsOf(component);
                if (root == null || root.panel == null || settings == null)
                {
                    census.Unattached++;
                    continue;
                }

                if (Elsewhere(settings))
                {
                    census.Elsewhere++;
                    continue;
                }

                var document = new Document
                {
                    Component = component,
                    Root = root,
                    Panel = root.panel,
                    Address = Addresses.Of(component.gameObject),
                    PanelOrder = settings.sortingOrder,
                    Order = component.sortingOrder,
                };
                if (sized)
                {
                    Map(document, width, height);
                }

                documents.Add(document);
            }

            census.Documents = documents.Count;
            foreach (Document document in documents)
            {
                document.Nested = new HashSet<VisualElement>(documents.Where(d => d != document && d.Root != document.Root && document.Root.Contains(d.Root)).Select(d => d.Root));
            }

            return documents.OrderByDescending(d => d.PanelOrder).ThenByDescending(d => d.Order).ToList();
        }

        // A nested document (parentUI) draws in its parent's panel.
        internal static PanelSettings SettingsOf(UIDocument component)
        {
            for (UIDocument d = component; d != null; d = d.parentUI)
            {
                if (d.parentUI == null)
                {
                    return d.panelSettings;
                }
            }

            return null;
        }

        // Panels drawn into a texture or by cameras in the world: not on the Game view's pixels.
        private static bool Elsewhere(PanelSettings settings)
        {
#if UNITY_6000_3_OR_NEWER
            if (settings.renderMode == PanelRenderMode.WorldSpace)
            {
                return true;
            }
#endif
            return settings.targetTexture != null;
        }

        // ScreenToPanel takes Game view pixels with y down and divides by the panel scale: two points invert it.
        private static void Map(Document document, int width, int height)
        {
            Vector2 a = RuntimePanelUtils.ScreenToPanel(document.Panel, Vector2.zero);
            Vector2 b = RuntimePanelUtils.ScreenToPanel(document.Panel, new Vector2(width, height));
            Vector2 span = b - a;
            if (Mathf.Abs(span.x) > 0.001f && Mathf.Abs(span.y) > 0.001f)
            {
                document.Origin = a;
                document.Scale = new Vector2(width / span.x, height / span.y);
            }
        }

        /// <summary>The `ug ui` items of every document; the census for the headline.</summary>
        public static Census Gather(List<UiItem> into, int width, int height, out string problem)
        {
            List<Document> documents = Documents(out Census census, out problem);
            if (problem != null)
            {
                return census;
            }

            foreach (Document document in documents)
            {
                Collect(document, document.Root, into, documents, width, height);
            }

            return census;
        }

        private static void Collect(Document document, VisualElement parent, List<UiItem> into, List<Document> documents, int width, int height)
        {
            for (int i = 0; i < parent.hierarchy.childCount; i++)
            {
                VisualElement element = parent.hierarchy[i];
                if (document.Nested.Contains(element) || element.resolvedStyle.display == DisplayStyle.None)
                {
                    continue;
                }

                string kind = KindOf(element);
                if (kind == null && (element.HasBubbleUpHandlers() || element.HasTrickleDownHandlers()))
                {
                    kind = "clickable";
                }

                if (kind != null)
                {
                    into.Add(Item(document, element, kind, documents, width, height));
                }

                if (kind == null || kind == "clickable")
                {
                    Collect(document, element, into, documents, width, height);
                }
            }
        }

        private static UiItem Item(Document document, VisualElement element, string kind, List<Document> documents, int width, int height)
        {
            Rect bound = element.worldBound;
            Vector2 center = document.ToScreen(bound.center);
            Vector2 size = new Vector2(bound.width * document.Scale.x, bound.height * document.Scale.y);
            int cuts = UiItem.Cuts;
            string label = LabelOf(element);
            var item = new UiItem
            {
                Address = AddressOf(document, element),
                Kind = kind,
                Detail = Detail(element, kind, label),
                Classes = Classes(element, 3),
                Label = label,
                Name = element.name,
                Center = center,
                Size = size,
                Toolkit = true,
            };
            item.LabelCut = UiItem.Cuts > cuts;

            if (!element.enabledInHierarchy)
            {
                item.State = "disabled";
            }
            else if (Hidden(element))
            {
                item.State = "hidden";
            }
            else if (float.IsNaN(center.x) || center.x < 0 || center.y < 0 || center.x > width || center.y > height)
            {
                item.State = "offscreen";
            }
            else
            {
                Target top = Pick(documents, center);
                if (top == null)
                {
                    item.State = "no pick target";
                }
                else if (top.Element != element && !element.Contains(top.Element))
                {
                    item.State = "blocked by " + top.Address;
                }
            }

            return item;
        }

        /// <summary>Hidden by `visibility` (inherited) or an opacity of 0 on it or above it.</summary>
        public static bool Hidden(VisualElement element)
        {
            if (!element.visible)
            {
                return true;
            }

            for (VisualElement e = element; e != null; e = e.hierarchy.parent)
            {
                if (e.resolvedStyle.opacity <= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The top element at a Game view point across the screen panels (highest sort order first), or null.</summary>
        public static Target Pick(List<Document> documents, Vector2 screen)
        {
            var tried = new HashSet<IPanel>();
            foreach (Document document in documents)
            {
                if (!tried.Add(document.Panel))
                {
                    continue;
                }

                VisualElement picked = document.Panel.Pick(document.ToPanel(screen));
                if (picked == null || picked == document.Panel.visualTree)
                {
                    continue;
                }

                Document owner = documents.Where(d => d.Panel == document.Panel && (d.Root == picked || d.Root.Contains(picked)))
                    .OrderByDescending(d => Depth(d.Root)).FirstOrDefault();
                if (owner == null)
                {
                    // Something of the panel outside every document (an open dropdown menu): it still takes the click.
                    return new Target { Document = document, Element = picked, Address = (picked.name.Length > 0 ? picked.name : picked.GetType().Name) + " (outside the documents)" };
                }

                return new Target { Document = owner, Element = picked, Address = AddressOf(owner, Listed(owner, picked)) };
            }

            return null;
        }

        private static int Depth(VisualElement element)
        {
            int depth = 0;
            for (VisualElement e = element.hierarchy.parent; e != null; e = e.hierarchy.parent)
            {
                depth++;
            }

            return depth;
        }

        // The element `ug ui` would name for a picked part: the control it is a part of, else itself.
        private static VisualElement Listed(Document document, VisualElement picked)
        {
            VisualElement named = picked;
            for (VisualElement e = picked; e != null && e != document.Root; e = e.hierarchy.parent)
            {
                if (KindOf(e) != null)
                {
                    named = e;
                }
            }

            return named;
        }

        /// <summary>The kind `ug ui` prints for a control, or null for anything else.</summary>
        public static string KindOf(VisualElement element)
        {
            switch (element)
            {
                case Button _:
                case RepeatButton _:
                    return "button";
                case Foldout _:
                    return "foldout";
                case RadioButton _:
                case RadioButtonGroup _:
                    return "radio";
                case BaseField<bool> _:
                    return "toggle";
                case Scroller _:
                    return "scrollbar";
                case BaseSlider<float> _:
                case BaseSlider<int> _:
                case MinMaxSlider _:
                    return "slider";
                case TextInputBaseField<string> _:
                    return "input";
                case BasePopupField<string, string> _:
                case EnumField _:
                    return "dropdown";
            }

            return IsField(element.GetType()) ? "field" : null;
        }

        private static bool IsField(Type type)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(BaseField<>))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The text a person would read on it: a button's text, a field's label, else the first text inside.</summary>
        public static string LabelOf(VisualElement element)
        {
            switch (element)
            {
                case Button button:
                    return UiItem.Cut(button.text);
                case Foldout foldout:
                    return UiItem.Cut(foldout.text);
            }

            string label = element.GetType().GetProperty("label", BindingFlags.Public | BindingFlags.Instance)?.GetValue(element) as string;
            if (!string.IsNullOrWhiteSpace(label))
            {
                return UiItem.Cut(label);
            }

            if (element is TextElement text && !string.IsNullOrWhiteSpace(text.text))
            {
                return UiItem.Cut(text.text);
            }

            foreach (TextElement inner in element.Query<TextElement>().Build())
            {
                if (!string.IsNullOrWhiteSpace(inner.text))
                {
                    return UiItem.Cut(inner.text);
                }
            }

            return "";
        }

        // What follows the kind on a `ug ui` line: the label, or the value for what holds one.
        private static string Detail(VisualElement element, string kind, string label)
        {
            switch (element)
            {
                case Foldout foldout:
                    return UiItem.Quote(label) + "  " + (foldout.value ? "open" : "closed");
                case RadioButtonGroup group:
                    {
                        List<string> choices = group.choices?.ToList() ?? new List<string>();
                        string chosen = group.value >= 0 && group.value < choices.Count ? choices[group.value] : "";
                        return UiItem.Quote(chosen) + " (" + Answer.Count(choices.Count, "option", "options") + ")";
                    }

                case BaseField<bool> toggle:
                    return UiItem.Quote(label) + "  " + (toggle.value ? "on" : "off");
                case Scroller scroller:
                    return UiItem.Number(scroller.value, false) + " (" + UiItem.Number(scroller.lowValue, false) + "-" + UiItem.Number(scroller.highValue, false) + ")";
                case BaseSlider<float> slider:
                    return UiItem.Number(slider.value, false) + " (" + UiItem.Number(slider.lowValue, false) + "-" + UiItem.Number(slider.highValue, false) + ")";
                case BaseSlider<int> whole:
                    return whole.value.ToString(CultureInfo.InvariantCulture) + " (" + whole.lowValue.ToString(CultureInfo.InvariantCulture) + "-" + whole.highValue.ToString(CultureInfo.InvariantCulture) + ")";
                case MinMaxSlider range:
                    return UiItem.Number(range.minValue, false) + "," + UiItem.Number(range.maxValue, false) + " (" + UiItem.Number(range.lowLimit, false) + "-" + UiItem.Number(range.highLimit, false) + ")";
                case TextInputBaseField<string> input:
                    return UiItem.Quote(input.value);
                case BasePopupField<string, string> popup:
                    return UiItem.Quote(popup.value) + " (" + Answer.Count(popup.choices?.Count ?? 0, "option", "options") + ")";
                case EnumField choice:
                    return UiItem.Quote(choice.value?.ToString() ?? "") + " (" + Answer.Count(choice.value == null ? 0 : Enum.GetNames(choice.value.GetType()).Length, "option", "options") + ")";
            }

            if (kind == "field")
            {
                object value = element.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(element);
                string shown = UiItem.Cut(Values.Show(value, Values.Caps.Compact, out _));
                return label.Length > 0 ? UiItem.Quote(label) + "  " + shown : shown;
            }

            return UiItem.Quote(label.Length > 0 ? label : element.name.Length > 0 ? element.name : element.GetType().Name);
        }

        /// <summary>`.primary .wide`: the element's own classes that are not Unity's (`unity-...`), at most <paramref name="most"/>.</summary>
        public static string Classes(VisualElement element, int most)
        {
            List<string> own = element.GetClasses().Where(c => !c.StartsWith("unity-", StringComparison.Ordinal)).ToList();
            string text = string.Join(" ", own.Take(most).Select(c => "." + c));
            return own.Count > most ? text + " +" + (own.Count - most).ToString(CultureInfo.InvariantCulture) : text;
        }

        /// <summary>
        /// An element address: the longest prefix that names a game object with a UIDocument, then
        /// segments. Null with a null error when the address names no document at all (the caller's
        /// game object error then stands); null with an error when the document does not have it.
        /// </summary>
        public static Target Find(string address, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(address))
            {
                return null;
            }

            address = address.Trim();
            for (int cut = address.LastIndexOf('/'); cut > 0; cut = address.LastIndexOf('/', cut - 1))
            {
                GameObject go = Addresses.Find(address.Substring(0, cut), out _);
                if (go == null)
                {
                    continue;
                }

                UIDocument component = go.GetComponent<UIDocument>();
                if (component == null)
                {
                    return null;
                }

                Document document = DocumentFor(component, out error);
                if (document == null)
                {
                    return null;
                }

                VisualElement element = Resolve(document, address.Substring(cut + 1), out error);
                return element == null ? null : new Target { Document = document, Element = element, Address = AddressOf(document, element) };
            }

            return null;
        }

        /// <summary>
        /// The document of a UIDocument: its screen panel's, else (world space, render texture) one
        /// without a Game view place; null and why when it has no panel at all.
        /// </summary>
        public static Document DocumentFor(UIDocument component, out string error)
        {
            List<Document> documents = Documents(out _, out error);
            if (error != null)
            {
                return null;
            }

            Document document = documents.FirstOrDefault(d => d.Component == component);
            if (document != null)
            {
                return document;
            }

            VisualElement root = component.isActiveAndEnabled ? component.rootVisualElement : null;
            if (root == null || root.panel == null || SettingsOf(component) == null)
            {
                error = Addresses.Of(component.gameObject) + "'s UIDocument has no panel (" + (component.isActiveAndEnabled ? "no PanelSettings or UXML" : "inactive or disabled") + ")";
                return null;
            }

            var others = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(d => d != component && d.isActiveAndEnabled && d.rootVisualElement != null && d.rootVisualElement != root && root.Contains(d.rootVisualElement));
            return new Document
            {
                Component = component,
                Root = root,
                Panel = root.panel,
                Address = Addresses.Of(component.gameObject),
                Nested = new HashSet<VisualElement>(others.Select(d => d.rootVisualElement)),
                OnScreen = false,
            };
        }

        /// <summary>The target for an element `ug` found some other way (the focus, a pick).</summary>
        public static Target Of(Document document, VisualElement element) =>
            new Target { Document = document, Element = element, Address = AddressOf(document, element) };

        private static VisualElement Resolve(Document document, string path, out string error)
        {
            error = null;
            string[] segments = path.Trim('/').Split('/');
            VisualElement scope = document.Root;
            string where = document.Address + "'s UI Toolkit document";
            foreach (string segment in segments)
            {
                if (segment.Length == 0)
                {
                    error = "no element path after " + document.Address;
                    return null;
                }

                List<VisualElement> matches = Match(document, scope, segment, out int index);
                if (matches.Count == 0)
                {
                    error = "not found: " + segment + " in " + where + Addresses.Near(Strip(segment), Descendants(document, scope).Select(e => e.name).Where(n => n.Length > 0));
                    return null;
                }

                if (index > 0)
                {
                    if (index > matches.Count)
                    {
                        error = "not found: " + segment + " in " + where + " (" + matches.Count.ToString(CultureInfo.InvariantCulture) + " match)";
                        return null;
                    }

                    scope = matches[index - 1];
                }
                else if (matches.Count > 1)
                {
                    error = "ambiguous: " + segment + " (" + matches.Count.ToString(CultureInfo.InvariantCulture) + " in " + where + "): "
                        + string.Join(", ", matches.Take(MaxCandidates).Select(e => AddressOf(document, e)))
                        + (matches.Count > MaxCandidates ? ", ... " + (matches.Count - MaxCandidates).ToString(CultureInfo.InvariantCulture) + " more: ug tree --ui " + (scope == document.Root ? document.Address : AddressOf(document, scope)) : "");
                    return null;
                }
                else
                {
                    scope = matches[0];
                }

                where = AddressOf(document, scope);
            }

            return scope;
        }

        // `Play`, `Button`, `Button.primary`, `.card`, each with an optional `#n`.
        private static List<VisualElement> Match(Document document, VisualElement scope, string segment, out int index)
        {
            index = 0;
            string text = segment;
            int hash = text.LastIndexOf('#');
            if (hash > 0 && int.TryParse(text.Substring(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
            {
                index = n;
                text = text.Substring(0, hash);
            }

            List<VisualElement> all = Descendants(document, scope).ToList();
            List<VisualElement> named = all.Where(e => e.name == text).ToList();
            if (named.Count > 0)
            {
                return named;
            }

            string[] parts = text.Split('.');
            string head = parts[0];
            IEnumerable<VisualElement> found = all;
            if (head.Length > 0)
            {
                List<VisualElement> byName = all.Where(e => string.Equals(e.name, head, StringComparison.OrdinalIgnoreCase)).ToList();
                found = byName.Count > 0
                    ? byName
                    : all.Where(e => string.Equals(e.GetType().Name, head, StringComparison.OrdinalIgnoreCase));
            }

            foreach (string name in parts.Skip(1).Where(p => p.Length > 0))
            {
                found = found.Where(e => e.ClassListContains(name));
            }

            return found.ToList();
        }

        private static string Strip(string segment)
        {
            int hash = segment.LastIndexOf('#');
            string text = hash > 0 ? segment.Substring(0, hash) : segment;
            int dot = text.IndexOf('.');
            return dot > 0 ? text.Substring(0, dot) : text;
        }

        /// <summary>The elements below <paramref name="scope"/> in tree order, leaving out nested documents.</summary>
        public static IEnumerable<VisualElement> Descendants(Document document, VisualElement scope)
        {
            for (int i = 0; i < scope.hierarchy.childCount; i++)
            {
                VisualElement child = scope.hierarchy[i];
                if (document.Nested != null && document.Nested.Contains(child))
                {
                    continue;
                }

                yield return child;
                foreach (VisualElement below in Descendants(document, child))
                {
                    yield return below;
                }
            }
        }

        /// <summary>The shortest address that resolves back to the element: `Menu/Play`, `Menu/Pause/Play`, `Menu/Button#3`.</summary>
        public static string AddressOf(Document document, VisualElement element)
        {
            if (element == document.Root)
            {
                return document.Address;
            }

            string segment = element.name.Length > 0 ? element.name : element.GetType().Name;
            List<VisualElement> everywhere = Match(document, document.Root, segment, out _);
            if (everywhere.Count == 1 && everywhere[0] == element)
            {
                return document.Address + "/" + segment;
            }

            for (VisualElement above = element.hierarchy.parent; above != null && above != document.Root; above = above.hierarchy.parent)
            {
                if (above.name.Length == 0)
                {
                    continue;
                }

                List<VisualElement> same = Match(document, document.Root, above.name, out _);
                if (same.Count != 1 || same[0] != above)
                {
                    continue;
                }

                List<VisualElement> inside = Match(document, above, segment, out _);
                if (inside.Count == 1 && inside[0] == element)
                {
                    return document.Address + "/" + above.name + "/" + segment;
                }
            }

            int index = everywhere.IndexOf(element);
            return document.Address + "/" + segment + (index < 0 ? "" : "#" + (index + 1).ToString(CultureInfo.InvariantCulture));
        }
    }
}
