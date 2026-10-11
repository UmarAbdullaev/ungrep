using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ungrep.Bridge
{
    /// <summary>
    /// `ug tree --ui`, `ug inspect` and `ug set` on UI Toolkit elements (SPEC-play-input, UI Toolkit).
    /// A set changes the live element only: an element is not a Unity object, so there is no Undo,
    /// nothing is dirty, and the document's UXML rebuilds it when it is enabled again.
    /// </summary>
    internal static partial class UiToolkit
    {
        private const int DefaultTreeDepth = 10;


        private static readonly PropertyInfo[] _styles = typeof(IStyle).GetProperties().Where(p => p.GetCustomAttribute<ObsoleteAttribute>() == null).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

        /// <summary>`/tree?ui=`: the visual tree under a document's game object or an element.</summary>
        public static Reply Tree(string address, string depthText)
        {
            int depth = int.TryParse(depthText, NumberStyles.None, CultureInfo.InvariantCulture, out int d) ? Math.Min(d, 50) : DefaultTreeDepth;
            Target target = TargetOf(address, out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            Document document = target.Document;
            var lines = new List<string>();
            int cuts = UiItem.Cuts;
            if (target.Element == document.Root)
            {
                UIDocument component = document.Component;
                PanelSettings settings = SettingsOf(component);
                string where = document.OnScreen ? "" : settings != null && settings.targetTexture != null ? "  render texture " + Path(settings.targetTexture) : "  world space";
                lines.Add(document.Address + "  UIDocument  " + Path(component.visualTreeAsset) + "  PanelSettings " + Path(settings)
                    + "  sort " + UiItem.Number(component.sortingOrder, false) + where);
            }
            else
            {
                lines.Add(TreeLine(document, target.Element, target.Address));
            }

            int left = 0;
            if (depth > 0)
            {
                Branch(document, target.Element, target.Element, 1, depth, lines, ref left);
            }

            if (left > 0)
            {
                lines.Add("... " + Answer.Count(left, "more element", "more elements") + ": ug tree --ui <element> or --depth to narrow");
            }

            if (UiItem.Cuts > cuts)
            {
                lines.Add(UiItem.CutNote);
            }

            return Answer.Lines(lines);
        }

        // A game object with a UIDocument (its root), or an element address.
        private static Target TargetOf(string address, out string error)
        {
            GameObject go = Addresses.Find(address, out string objectError);
            if (go != null)
            {
                UIDocument component = go.GetComponent<UIDocument>();
                if (component == null)
                {
                    error = Addresses.Of(go) + " has no UIDocument";
                    return null;
                }

                Document document = DocumentFor(component, out error);
                return document == null ? null : new Target { Document = document, Element = document.Root, Address = document.Address };
            }

            Target target = Find(address, out error);
            if (target == null && error == null)
            {
                error = objectError;
            }

            return target;
        }

        private static void Branch(Document document, VisualElement parent, VisualElement given, int level, int depth, List<string> lines, ref int left)
        {
            for (int i = 0; i < parent.hierarchy.childCount; i++)
            {
                VisualElement child = parent.hierarchy[i];
                if (lines.Count >= ReadOps.MaxTreeLines)
                {
                    left += 1 + Below(child);
                    continue;
                }

                bool nested = document.Nested.Contains(child);
                bool closed = nested || child.resolvedStyle.display == DisplayStyle.None || (KindOf(child) != null && child != given);
                string line = new string(' ', level * 2) + TreeLine(document, child, Short(document, child));
                if (nested)
                {
                    line += "  (its own UIDocument)";
                }
                else if (closed && child.hierarchy.childCount > 0)
                {
                    line += "  (" + Answer.Count(Below(child), "element", "elements") + " inside)";
                }

                lines.Add(line);
                if (!closed && level < depth)
                {
                    Branch(document, child, given, level + 1, depth, lines, ref left);
                }
                else if (!closed)
                {
                    left += Below(child);
                }
            }
        }

        private static int Below(VisualElement element)
        {
            int count = 0;
            for (int i = 0; i < element.hierarchy.childCount; i++)
            {
                count += 1 + Below(element.hierarchy[i]);
            }

            return count;
        }

        // The address after the document's: `Play`, `Pause/Play`, `Button#3`.
        private static string Short(Document document, VisualElement element)
        {
            string address = AddressOf(document, element);
            return address.StartsWith(document.Address + "/", StringComparison.Ordinal) ? address.Substring(document.Address.Length + 1) : address;
        }

        private static string TreeLine(Document document, VisualElement element, string name)
        {
            var line = new StringBuilder(name).Append("  ").Append(element.GetType().Name);
            string classes = Classes(element, 3);
            if (classes.Length > 0)
            {
                line.Append("  ").Append(classes);
            }

            string text = OwnText(element);
            if (text != null)
            {
                line.Append("  ").Append(UiItem.Quote(text));
            }

            line.Append(Place(document, element));
            var states = new List<string>();
            if (!element.enabledSelf)
            {
                states.Add("disabled");
            }

            if (element.resolvedStyle.visibility == Visibility.Hidden && (element.hierarchy.parent == null || element.hierarchy.parent.resolvedStyle.visibility != Visibility.Hidden))
            {
                states.Add("hidden");
            }

            if (element.resolvedStyle.display == DisplayStyle.None)
            {
                states.Add("display none");
            }

            if (element.pickingMode == PickingMode.Ignore)
            {
                states.Add("pick ignore");
            }

            if (states.Count > 0)
            {
                line.Append("  ").Append(string.Join(", ", states));
            }

            return line.ToString();
        }

        // A text element's text or a control's label; null when it has none.
        private static string OwnText(VisualElement element)
        {
            if (element is TextElement text)
            {
                return string.IsNullOrEmpty(text.text) ? null : text.text;
            }

            if (element is Foldout foldout)
            {
                return foldout.text;
            }

            string label = element.GetType().GetProperty("label", BindingFlags.Public | BindingFlags.Instance)?.GetValue(element) as string;
            return string.IsNullOrEmpty(label) ? null : label;
        }

        // `  525,349  678x114` in Game view pixels; nothing before a layout or off the Game view.
        private static string Place(Document document, VisualElement element)
        {
            Rect bound = element.worldBound;
            if (!document.OnScreen || float.IsNaN(bound.x) || float.IsNaN(bound.width))
            {
                return "";
            }

            Vector2 center = document.ToScreen(bound.center);
            return "  " + Where(center) + "  " + Mathf.RoundToInt(bound.width * document.Scale.x).ToString(CultureInfo.InvariantCulture)
                + "x" + Mathf.RoundToInt(bound.height * document.Scale.y).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>`ug inspect` of an element: what it is, where, its state, value, style sheets, inline and resolved styles.</summary>
        public static Reply Inspect(Target target)
        {
            VisualElement element = target.Element;
            Document document = target.Document;
            var lines = new List<string>();
            string text = OwnText(element);
            lines.Add(target.Address + "  " + element.GetType().FullName + (text == null ? "" : "  " + UiItem.Whole(text))
                + "  (" + document.Address + ": " + Path(document.Component.visualTreeAsset) + ")");
            List<string> classes = element.GetClasses().ToList();
            if (classes.Count > 0)
            {
                lines.Add("classes  " + string.Join(" ", classes.Select(c => "." + c)));
            }

            Rect bound = element.worldBound;
            if (!float.IsNaN(bound.x))
            {
                string panel = "panel " + UiItem.Number(bound.x, false) + "," + UiItem.Number(bound.y, false) + " " + UiItem.Number(bound.width, false) + "x" + UiItem.Number(bound.height, false);
                string place = Place(document, element);
                lines.Add("place" + (place.Length > 0 ? place + "  (" + panel + ")" : "  " + panel + (document.OnScreen ? "" : " (not on the Game view)")));
            }

            lines.Add("state  " + (element.enabledInHierarchy ? "enabled" : element.enabledSelf ? "disabled (by a parent)" : "disabled")
                + "  " + (Hidden(element) ? "hidden" : "visible") + "  display " + Css(element.resolvedStyle.display.ToString())
                + "  " + (element.focusable ? "focusable" : "not focusable") + "  picking " + Css(element.pickingMode.ToString())
                + (element.panel?.focusController?.focusedElement == element ? "  focused" : ""));
            if (!string.IsNullOrEmpty(element.tooltip))
            {
                lines.Add("tooltip  " + UiItem.Whole(element.tooltip));
            }

            string value = ValueText(element, true);
            if (value.Length > 0)
            {
                lines.Add("value  " + value + Choices(element));
            }

            var sheets = new List<string>();
            for (VisualElement e = element; e != null; e = e == document.Root ? null : e.hierarchy.parent)
            {
                for (int i = 0; i < e.styleSheets.count; i++)
                {
                    sheets.Add(Path(e.styleSheets[i]) + (e == element ? "" : " (on " + (e == document.Root ? document.Address : Short(document, e)) + ")"));
                }
            }

            PanelSettings settings = SettingsOf(document.Component);
            if (settings != null && settings.themeStyleSheet != null)
            {
                sheets.Add("theme " + Path(settings.themeStyleSheet));
            }

            if (sheets.Count > 0)
            {
                lines.Add("style sheets  " + string.Join(", ", sheets.Distinct()));
            }

            List<string> inline = _styles.Select(p => (Property: p, Value: p.GetValue(element.style)))
                .Where(s => KeywordOf(s.Value) != StyleKeyword.Null)
                .Select(s => UssName(s.Property.Name) + " " + StyleText(s.Value))
                .ToList();
            if (inline.Count > 0)
            {
                lines.Add("inline  " + string.Join(", ", inline));
            }

            IResolvedStyle r = element.resolvedStyle;
            lines.Add("size  width " + Pixels(r.width) + "  height " + Pixels(r.height) + "  min " + StyleText(r.minWidth) + "," + StyleText(r.minHeight)
                + "  max " + StyleText(r.maxWidth) + "," + StyleText(r.maxHeight));
            lines.Add("position  " + Css(r.position.ToString()) + "  left " + Pixels(r.left) + "  top " + Pixels(r.top) + "  right " + Pixels(r.right) + "  bottom " + Pixels(r.bottom));
            lines.Add("margin  " + Four(r.marginTop, r.marginRight, r.marginBottom, r.marginLeft) + "  padding " + Four(r.paddingTop, r.paddingRight, r.paddingBottom, r.paddingLeft));
            lines.Add("border  width " + Four(r.borderTopWidth, r.borderRightWidth, r.borderBottomWidth, r.borderLeftWidth)
                + "  radius " + Four(r.borderTopLeftRadius, r.borderTopRightRadius, r.borderBottomRightRadius, r.borderBottomLeftRadius)
                + "  color " + ColorText(r.borderTopColor));
            lines.Add("flex  direction " + Css(r.flexDirection.ToString()) + "  wrap " + Css(r.flexWrap.ToString()) + "  grow " + UiItem.Number(r.flexGrow, false)
                + "  shrink " + UiItem.Number(r.flexShrink, false) + "  basis " + StyleText(r.flexBasis) + "  align-items " + Css(r.alignItems.ToString())
                + "  justify-content " + Css(r.justifyContent.ToString()) + "  align-self " + Css(r.alignSelf.ToString()));
            lines.Add("look  background-color " + ColorText(r.backgroundColor) + "  color " + ColorText(r.color) + "  opacity " + UiItem.Number(r.opacity, false)
                + "  visibility " + Css(r.visibility.ToString()) + (KeywordOf(element.style.overflow) == StyleKeyword.Null ? "" : "  overflow " + StyleText(element.style.overflow)));
            lines.Add("text  font-size " + Pixels(r.fontSize) + "  -unity-font-style " + Css(r.unityFontStyleAndWeight.ToString())
                + "  -unity-text-align " + Css(r.unityTextAlign.ToString()) + "  white-space " + Css(r.whiteSpace.ToString()));
            string image = Resolved(element, "backgroundImage");
            if (image != "none")
            {
                lines.Add("background-image  " + image);
            }

            return Answer.Lines(lines);
        }

        private static string Choices(VisualElement element)
        {
            switch (element)
            {
                case RadioButtonGroup group:
                    return "  options " + Options(group.choices ?? Enumerable.Empty<string>());
                case BasePopupField<string, string> popup:
                    return "  options " + Options(popup.choices ?? new List<string>());
                case EnumField choice when choice.value != null:
                    return "  options " + Options(Enum.GetNames(choice.value.GetType()));
                case BaseSlider<float> slider:
                    return "  range " + UiItem.Number(slider.lowValue, false) + "-" + UiItem.Number(slider.highValue, false);
                case BaseSlider<int> whole:
                    return "  range " + whole.lowValue.ToString(CultureInfo.InvariantCulture) + "-" + whole.highValue.ToString(CultureInfo.InvariantCulture);
                case MinMaxSlider range:
                    return "  limits " + UiItem.Number(range.lowLimit, false) + "-" + UiItem.Number(range.highLimit, false);
                case Scroller scroller:
                    return "  range " + UiItem.Number(scroller.lowValue, false) + "-" + UiItem.Number(scroller.highValue, false);
                default:
                    return "";
            }
        }

        /// <summary>`ug set <element> <property> <value>` on the live element.</summary>
        public static Reply Set(Target target, string property, string value)
        {
            VisualElement element = target.Element;
            string name = (property ?? "").Trim();
            string keep = "  (live element only: not saved, no undo" + (target.Document.Component.visualTreeAsset == null ? ")" : "; edit " + Path(target.Document.Component.visualTreeAsset) + " or .uss to keep it)");
            if (name == "value")
            {
                Reply reply = SetValue(target, value);
                return Answer.IsError(reply) ? reply : Answer.Line(reply.Lines[0] + keep);
            }

            string before;
            string after;
            string error = null;
            switch (name)
            {
                case "text":
                    {
                        PropertyInfo text = element is TextElement || element is Foldout ? element.GetType().GetProperty("text") : element.GetType().GetProperty("label");
                        if (text == null || !text.CanWrite)
                        {
                            return Answer.Error(target.Address + " (" + element.GetType().Name + ") has no text or label");
                        }

                        before = UiItem.Quote(text.GetValue(element) as string ?? "");
                        text.SetValue(element, value);
                        after = UiItem.Quote(text.GetValue(element) as string ?? "");
                        break;
                    }

                case "tooltip":
                    before = UiItem.Quote(element.tooltip ?? "");
                    element.tooltip = value;
                    after = UiItem.Quote(element.tooltip);
                    break;
                case "name":
                    before = element.name;
                    element.name = value.Trim();
                    after = element.name;
                    break;
                case "enabled":
                    {
                        bool? on = Switch(value.Trim(), "true", "false");
                        if (on == null)
                        {
                            return Answer.Error("enabled takes true or false, not " + value);
                        }

                        before = element.enabledSelf ? "true" : "false";
                        element.SetEnabled(on.Value);
                        after = element.enabledSelf ? "true" : "false";
                        break;
                    }

                case "display":
                case "visible":
                    {
                        string t = value.Trim().ToLowerInvariant();
                        bool? shown = t == "flex" || t == "true" || t == "on" ? true : t == "none" || t == "false" || t == "off" ? false : (bool?)null;
                        if (shown == null)
                        {
                            return Answer.Error(name + " takes flex or none (true, false), not " + value);
                        }

                        name = "display";
                        before = Css(element.resolvedStyle.display.ToString());
                        element.style.display = shown.Value ? DisplayStyle.Flex : DisplayStyle.None;
                        after = shown.Value ? "flex" : "none";
                        break;
                    }

                case "visibility":
                    {
                        string t = value.Trim().ToLowerInvariant();
                        if (t != "visible" && t != "hidden")
                        {
                            return Answer.Error("visibility takes visible or hidden, not " + value);
                        }

                        before = Css(element.resolvedStyle.visibility.ToString());
                        element.style.visibility = t == "visible" ? Visibility.Visible : Visibility.Hidden;
                        after = t;
                        break;
                    }

                case "class":
                case "classes":
                    name = "class";
                    before = string.Join(" ", element.GetClasses().Where(c => !c.StartsWith("unity-", StringComparison.Ordinal)).Select(c => "." + c));
                    error = ChangeClasses(element, value);
                    after = string.Join(" ", element.GetClasses().Where(c => !c.StartsWith("unity-", StringComparison.Ordinal)).Select(c => "." + c));
                    before = before.Length == 0 ? "(none)" : before;
                    after = after.Length == 0 ? "(none)" : after;
                    break;
                default:
                    if (!name.StartsWith("style.", StringComparison.Ordinal))
                    {
                        return Answer.Error("an element takes text, tooltip, name, enabled, display, visibility, class, value or style.<name> (style.background-color), not " + name);
                    }

                    return SetStyle(target, name.Substring("style.".Length), value, keep);
            }

            return error != null
                ? Answer.Error(error)
                : Answer.Line(target.Address + "  " + name + "  " + before + " -> " + after + keep);
        }

        // `+a -b ~c`, a bare name adds.
        private static string ChangeClasses(VisualElement element, string value)
        {
            string[] words = value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                return "class takes +name, -name or ~name (add, remove, toggle), not " + value;
            }

            foreach (string word in words)
            {
                char sign = word[0];
                string name = (sign == '+' || sign == '-' || sign == '~' ? word.Substring(1) : word).TrimStart('.');
                if (name.Length == 0)
                {
                    return "no class name in " + word;
                }

                if (sign == '-')
                {
                    element.RemoveFromClassList(name);
                }
                else if (sign == '~')
                {
                    element.ToggleInClassList(name);
                }
                else
                {
                    element.AddToClassList(name);
                }
            }

            return null;
        }

        private static Reply SetStyle(Target target, string styleName, string value, string keep)
        {
            VisualElement element = target.Element;
            PropertyInfo property = StyleProperty(styleName);
            if (property == null)
            {
                return Answer.Error("no style " + styleName + Addresses.Near(styleName, _styles.Select(p => UssName(p.Name))));
            }

            string uss = UssName(property.Name);
            string before = Resolved(element, property.Name);
            string error = ParseStyle(property.PropertyType, value.Trim(), out object parsed);
            if (error != null)
            {
                return Answer.Error("style." + uss + ": " + error);
            }

            property.SetValue(element.style, parsed);
            Call("UpdatePanels");
            return Answer.Line(target.Address + "  style." + uss + "  " + before + " -> " + Resolved(element, property.Name) + keep);
        }

        // `background-color`, `-unity-font-style`, `backgroundColor` or `unityFontStyleAndWeight`.
        private static PropertyInfo StyleProperty(string name)
        {
            string key = name.Trim().TrimStart('-').Replace("-", "");
            if (string.Equals(key, "unityfontstyle", StringComparison.OrdinalIgnoreCase))
            {
                key = "unityFontStyleAndWeight";
            }

            return _styles.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The USS name of an IStyle property: `backgroundColor` is `background-color`, `unityTextAlign` is `-unity-text-align`.</summary>
        public static string UssName(string property)
        {
            if (property == "unityFontStyleAndWeight")
            {
                return "-unity-font-style";
            }

            var name = new StringBuilder();
            foreach (char c in property)
            {
                if (char.IsUpper(c))
                {
                    name.Append('-').Append(char.ToLowerInvariant(c));
                }
                else
                {
                    name.Append(c);
                }
            }

            string text = name.ToString();
            return text.StartsWith("unity-", StringComparison.Ordinal) ? "-" + text : text;
        }

        // The resolved value of a style when IResolvedStyle has it, else the inline one.
        private static string Resolved(VisualElement element, string property)
        {
            PropertyInfo resolved = typeof(IResolvedStyle).GetProperty(property);
            if (resolved != null)
            {
                return StyleText(resolved.GetValue(element.resolvedStyle));
            }

            object inline = typeof(IStyle).GetProperty(property)?.GetValue(element.style);
            return inline == null || KeywordOf(inline) == StyleKeyword.Null ? "(not set inline)" : StyleText(inline);
        }

        // A StyleX value from text: `initial`/`null` keywords, else the inner value by its type.
        private static string ParseStyle(Type styleType, string text, out object parsed)
        {
            parsed = null;
            string lower = text.ToLowerInvariant();
            StyleKeyword? keyword = lower == "initial" ? StyleKeyword.Initial : lower == "null" ? StyleKeyword.Null : (StyleKeyword?)null;
            if (keyword == null && lower == "auto" && styleType == typeof(StyleLength))
            {
                keyword = StyleKeyword.Auto;
            }
            else if (keyword == null && lower == "none" && (styleType == typeof(StyleLength) || styleType == typeof(StyleBackground)))
            {
                keyword = StyleKeyword.None;
            }

            if (keyword != null)
            {
                parsed = Activator.CreateInstance(styleType, keyword.Value);
                return null;
            }

            Type inner = Inner(styleType);
            object value;
            if (inner == typeof(Color))
            {
                if (!TryCss(text, out Color color))
                {
                    return "not a colour (#2eb89e, rgb(46, 184, 158), rgba(46, 184, 158, 0.5), red): " + text;
                }

                value = color;
            }
            else if (inner == typeof(Length))
            {
                if (!TryLength(text, out Length length))
                {
                    return "not a length (12, 12px, 50%, auto): " + text;
                }

                value = length;
            }
            else if (inner == typeof(float))
            {
                if (!float.TryParse(text.EndsWith("px", StringComparison.Ordinal) ? text.Substring(0, text.Length - 2) : text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                {
                    return "not a number: " + text;
                }

                value = number;
            }
            else if (inner == typeof(int))
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                {
                    return "not a whole number: " + text;
                }

                value = number;
            }
            else if (inner.IsEnum)
            {
                string key = lower.Replace("-", "");
                string name = Enum.GetNames(inner).FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
                if (name == null)
                {
                    return "takes " + string.Join(", ", Enum.GetNames(inner).Select(n => Css(n))) + ", not " + text;
                }

                value = Enum.Parse(inner, name);
            }
            else if (inner == typeof(Background))
            {
                UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(text);
                Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(text);
                switch (sprite != null && !(asset is Texture2D) ? sprite : asset)
                {
                    case Texture2D texture:
                        value = Background.FromTexture2D(texture);
                        break;
                    case Sprite one:
                        value = Background.FromSprite(one);
                        break;
                    case RenderTexture render:
                        value = Background.FromRenderTexture(render);
                        break;
                    case VectorImage vector:
                        value = Background.FromVectorImage(vector);
                        break;
                    default:
                        return "takes a texture, sprite, render texture or vector image path (Assets/UI/Icon.png), not " + text;
                }
            }
            else if (inner == typeof(Font))
            {
                Font font = AssetDatabase.LoadAssetAtPath<Font>(text);
                if (font == null)
                {
                    return "takes a font asset path (Assets/Fonts/Inter.ttf), not " + text;
                }

                value = font;
            }
            else if (inner == typeof(FontDefinition))
            {
                UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(text);
                if (asset is Font font)
                {
                    value = FontDefinition.FromFont(font);
                }
                else if (asset != null && typeof(FontDefinition).GetMethod("FromSDFFont", BindingFlags.Public | BindingFlags.Static) is MethodInfo sdf
                    && sdf.GetParameters()[0].ParameterType.IsInstanceOfType(asset))
                {
                    value = sdf.Invoke(null, new object[] { asset });
                }
                else
                {
                    return "takes a font or font asset path, not " + text;
                }
            }
            else
            {
                return "is a " + styleType.Name + ": ug sets colours, lengths, numbers, keywords, images and fonts (initial or null drop the inline value)";
            }

            parsed = Activator.CreateInstance(styleType, value);
            return null;
        }

        // The `value` type of a StyleX struct (Color for StyleColor, T for StyleEnum<T>).
        private static Type Inner(Type styleType) =>
            styleType.GetProperty("value", BindingFlags.Public | BindingFlags.Instance)?.PropertyType ?? typeof(string);

        private static bool TryCss(string text, out Color color)
        {
            string t = text.Trim().ToLowerInvariant();
            if (t.StartsWith("rgb", StringComparison.Ordinal) && t.EndsWith(")", StringComparison.Ordinal) && t.IndexOf('(') > 0)
            {
                string[] parts = t.Substring(t.IndexOf('(') + 1).TrimEnd(')').Split(',');
                var n = new float[4] { 0, 0, 0, 1 };
                if ((parts.Length != 3 && parts.Length != 4) || parts.Select((p, i) => float.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])).Any(ok => !ok))
                {
                    color = default;
                    return false;
                }

                color = new Color(n[0] / 255f, n[1] / 255f, n[2] / 255f, n[3]);
                return true;
            }

            return Values.TryColor(text, out color);
        }

        private static bool TryLength(string text, out Length length)
        {
            string t = text.Trim().ToLowerInvariant();
            bool percent = t.EndsWith("%", StringComparison.Ordinal);
            string number = percent ? t.Substring(0, t.Length - 1) : t.EndsWith("px", StringComparison.Ordinal) ? t.Substring(0, t.Length - 2) : t;
            bool ok = float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float value);
            length = new Length(value, percent ? LengthUnit.Percent : LengthUnit.Pixel);
            return ok;
        }

        private static StyleKeyword KeywordOf(object styleValue) =>
            styleValue?.GetType().GetProperty("keyword", BindingFlags.Public | BindingFlags.Instance)?.GetValue(styleValue) is StyleKeyword keyword ? keyword : StyleKeyword.Undefined;

        // `FlexStart` as USS writes it: `flex-start`.
        private static string Css(string name) => UssName(char.ToLowerInvariant(name[0]) + name.Substring(1)).TrimStart('-');

        /// <summary>A style value as USS writes it: `rgb(46, 184, 158)`, `12px`, `50%`, `auto`, `flex-start`, an asset path.</summary>
        public static string StyleText(object value)
        {
            if (value == null)
            {
                return "none";
            }

            // A StyleX struct holds a keyword (auto, none, initial, null) or, with the keyword Undefined, a value.
            bool style = value.GetType().Name.StartsWith("Style", StringComparison.Ordinal);
            StyleKeyword keyword = style ? KeywordOf(value) : StyleKeyword.Undefined;
            if (keyword != StyleKeyword.Undefined)
            {
                return Css(keyword.ToString());
            }

            PropertyInfo inner = style ? value.GetType().GetProperty("value", BindingFlags.Public | BindingFlags.Instance) : null;
            object shown = inner != null ? inner.GetValue(value) : value;
            switch (shown)
            {
                case Color color:
                    return ColorText(color);
                case Length length:
                    return length.unit == LengthUnit.Percent ? UiItem.Number(length.value, false) + "%" : length.unit == LengthUnit.Pixel ? UiItem.Number(length.value, false) + "px" : length.ToString();
                case float number:
                    return float.IsNaN(number) ? "auto" : UiItem.Number(number, false);
                case int whole:
                    return whole.ToString(CultureInfo.InvariantCulture);
                case Enum choice:
                    return Css(choice.ToString());
                case Background background:
                    {
                        UnityEngine.Object asset = background.texture != null ? background.texture : background.sprite != null ? background.sprite
                            : background.renderTexture != null ? background.renderTexture : (UnityEngine.Object)background.vectorImage;
                        return asset == null ? "none" : Path(asset);
                    }

                case FontDefinition font:
                    return font.font != null ? Path(font.font) : font.fontAsset != null ? Path(font.fontAsset) : "none";
                case UnityEngine.Object asset:
                    return Path(asset);
            }

            return Values.Show(shown, Values.Caps.Full, out _);
        }

        private static string ColorText(Color color)
        {
            string rgb = Mathf.RoundToInt(color.r * 255).ToString(CultureInfo.InvariantCulture) + ", " + Mathf.RoundToInt(color.g * 255).ToString(CultureInfo.InvariantCulture)
                + ", " + Mathf.RoundToInt(color.b * 255).ToString(CultureInfo.InvariantCulture);
            return color.a >= 0.999f ? "rgb(" + rgb + ")" : "rgba(" + rgb + ", " + color.a.ToString("0.##", CultureInfo.InvariantCulture) + ")";
        }

        private static string Pixels(float value) => float.IsNaN(value) ? "auto" : UiItem.Number(value, false);

        private static string Four(float top, float right, float bottom, float left)
        {
            if (top == right && right == bottom && bottom == left)
            {
                return Pixels(top);
            }

            return Pixels(top) + " " + Pixels(right) + " " + Pixels(bottom) + " " + Pixels(left);
        }

        // An asset's path, or its name for one that is not an asset.
        private static string Path(UnityEngine.Object asset)
        {
            if (asset == null)
            {
                return "none";
            }

            string path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? "\"" + asset.name + "\"" : path;
        }
    }
}
