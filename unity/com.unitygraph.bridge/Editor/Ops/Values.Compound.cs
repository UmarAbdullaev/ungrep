using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// The values `ug set` reads from text beyond numbers and references (SPEC-editor-ops, Endpoints):
    /// bounds, curves, gradients, hashes, whole structs, `[a, b]` lists and `SerializeReference` types.
    /// Each reads what <see cref="Values.Format"/> prints, so a value read can be set back.
    /// </summary>
    internal static partial class Values
    {
        private const string CurveHelp = "not a curve: keys time:value like `0:0, 1:1`, optionally led by linear, constant or smooth; one number is a flat curve: ";

        private static readonly Regex _fieldStart = new Regex(@"^\s*([A-Za-z_]\w*)\s*:(.*)$", RegexOptions.Singleline);

        private static string FormatCurve(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0)
            {
                return "curve empty";
            }

            Keyframe[] keys = curve.keys;
            return "curve " + string.Join(", ", keys.Take(MaxItems).Select(k => Number(k.time) + ":" + Number(k.value)))
                + (keys.Length > MaxItems ? ", ... " + (keys.Length - MaxItems).ToString(CultureInfo.InvariantCulture) + " more" : "");
        }

        private static string FormatGradient(Gradient gradient)
        {
            if (gradient == null)
            {
                return "gradient";
            }

            var times = new SortedSet<float>(gradient.colorKeys.Select(k => k.time).Concat(gradient.alphaKeys.Select(k => k.time)));
            return "gradient " + (gradient.mode == GradientMode.Fixed ? "fixed " : "")
                + string.Join(", ", times.Take(MaxItems).Select(t => "#" + ColorUtility.ToHtmlStringRGBA(GradientAt(gradient, t)).ToLowerInvariant() + "@" + Number(Mathf.Round(t * 10000f) / 10000f)));
        }

        // The color at a key time; at an exact key its own color, so a fixed gradient reads back as written.
        private static Color GradientAt(Gradient gradient, float time)
        {
            Color color = gradient.Evaluate(time);
            foreach (GradientColorKey key in gradient.colorKeys)
            {
                if (key.time == time)
                {
                    color = new Color(key.color.r, key.color.g, key.color.b, color.a);
                }
            }

            foreach (GradientAlphaKey key in gradient.alphaKeys)
            {
                if (key.time == time)
                {
                    color.a = key.alpha;
                }
            }

            return color;
        }

        private static string ParseBounds(SerializedProperty p, string text)
        {
            string numbers = text.Replace("center", " ").Replace("size", ",").Replace("(", " ").Replace(")", " ");
            if (!TryNumbers(numbers, 6, out float[] n))
            {
                return "not bounds (`center 0, 1, 0 size 2, 2, 2` or six numbers: center then size): " + text;
            }

            p.boundsValue = new Bounds(new Vector3(n[0], n[1], n[2]), new Vector3(n[3], n[4], n[5]));
            return null;
        }

        private static string ParseCurve(SerializedProperty p, string text)
        {
            string body = Lead(text, "curve");
            string mode = "smooth";
            foreach (string word in new[] { "smooth", "linear", "constant" })
            {
                string rest = Lead(body, word);
                if (rest != body)
                {
                    mode = word;
                    body = rest;
                    break;
                }
            }

            if (body.Length == 0 || body == "empty")
            {
                p.animationCurveValue = new AnimationCurve();
                return null;
            }

            if (float.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out float flat))
            {
                p.animationCurveValue = AnimationCurve.Constant(0f, 1f, flat);
                return null;
            }

            var keys = new List<Keyframe>();
            foreach (string part in body.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] tv = part.Split(':');
                if (tv.Length != 2 || !float.TryParse(tv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float time)
                    || !float.TryParse(tv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    return CurveHelp + text;
                }

                keys.Add(new Keyframe(time, value));
            }

            keys.Sort((a, b) => a.time.CompareTo(b.time));
            for (int i = 1; i < keys.Count; i++)
            {
                if (keys[i].time == keys[i - 1].time)
                {
                    return "two keys at time " + Number(keys[i].time) + ": " + text;
                }
            }

            var curve = new AnimationCurve(keys.ToArray());
            for (int i = 0; i < curve.length; i++)
            {
                if (mode == "smooth")
                {
                    curve.SmoothTangents(i, 0f);
                    continue;
                }

                AnimationUtility.TangentMode tangents = mode == "linear" ? AnimationUtility.TangentMode.Linear : AnimationUtility.TangentMode.Constant;
                AnimationUtility.SetKeyLeftTangentMode(curve, i, tangents);
                AnimationUtility.SetKeyRightTangentMode(curve, i, tangents);
            }

            p.animationCurveValue = curve;
            return null;
        }

        private static string ParseGradient(SerializedProperty p, string text)
        {
            string body = Lead(text, "gradient");
            GradientMode mode = GradientMode.Blend;
            string fixedRest = Lead(body, "fixed");
            if (fixedRest != body)
            {
                mode = GradientMode.Fixed;
                body = fixedRest;
            }
            else
            {
                body = Lead(body, "blend");
            }

            string[] stops = body.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string help = "not a gradient: stops `#ff0000@0, #0000ff@1` (color@time, time 0 to 1; without @ they are spread evenly), optionally led by fixed: ";
            if (stops.Length == 0 || stops.Length > 8)
            {
                return stops.Length == 0 ? help + text : "a gradient has at most 8 stops: " + text;
            }

            var colors = new List<GradientColorKey>();
            var alphas = new List<GradientAlphaKey>();
            for (int i = 0; i < stops.Length; i++)
            {
                int at = stops[i].IndexOf('@');
                string colorText = at < 0 ? stops[i] : stops[i].Substring(0, at);
                float time = stops.Length == 1 ? 0f : i / (float)(stops.Length - 1);
                if (!TryColor(colorText, out Color color) || (at >= 0 && !float.TryParse(stops[i].Substring(at + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out time))
                    || time < 0f || time > 1f)
                {
                    return help + text;
                }

                colors.Add(new GradientColorKey(color, time));
                alphas.Add(new GradientAlphaKey(color.a, time));
            }

            var gradient = new Gradient { mode = mode };
            gradient.SetKeys(colors.OrderBy(k => k.time).ToArray(), alphas.OrderBy(k => k.time).ToArray());
            p.gradientValue = gradient;
            return null;
        }

        private static string ParseHash(SerializedProperty p, string text)
        {
            if (text.Length != 32 || !text.All(Uri.IsHexDigit))
            {
                return "not a hash (32 hex digits): " + text;
            }

            p.hash128Value = Hash128.Parse(text);
            return null;
        }

        // `TypeName`, `TypeName {field: value}`, `{field: value}` (keeps the type) or `null`.
        private static string ParseManaged(SerializedProperty p, string text)
        {
            int brace = text.IndexOf('{');
            string typeName = (brace < 0 ? text : text.Substring(0, brace)).Trim();
            string fields = brace < 0 ? null : text.Substring(brace);
            if (typeName == "null" && fields == null)
            {
                p.managedReferenceValue = null;
                return null;
            }

            if (typeName.Length == 0 && string.IsNullOrEmpty(p.managedReferenceFullTypename))
            {
                return "no type yet: `TypeName` or `TypeName {field: value}`" + ManagedChoices(p);
            }

            if (typeName.Length > 0)
            {
                Type type = ManagedType(p, typeName, out string error);
                if (type == null)
                {
                    return error;
                }

                if (!string.Equals(p.managedReferenceFullTypename, ManagedName(type), StringComparison.Ordinal))
                {
                    try
                    {
                        p.managedReferenceValue = Activator.CreateInstance(type);
                    }
                    catch (Exception e) when (e is MissingMethodException || e is System.Reflection.TargetInvocationException)
                    {
                        return type.Name + " has no public parameterless constructor";
                    }
                }
            }

            return fields == null ? null : ParseFields(p, fields);
        }

        private static string ManagedName(Type type) => type.Assembly.GetName().Name + " " + type.FullName.Replace('+', '/');

        private static IEnumerable<Type> ManagedCandidates(SerializedProperty p)
        {
            string[] parts = (p.managedReferenceFieldTypename ?? "").Split(' ');
            Type baseType = parts.Length == 2 ? Type.GetType(parts[1].Replace('/', '+') + ", " + parts[0]) : null;
            if (baseType == null)
            {
                return Enumerable.Empty<Type>();
            }

            return TypeCache.GetTypesDerivedFrom(baseType).Concat(new[] { baseType })
                .Where(t => !t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition && !typeof(UnityEngine.Object).IsAssignableFrom(t))
                .OrderBy(t => t.Name, StringComparer.Ordinal);
        }

        private static string ManagedChoices(SerializedProperty p)
        {
            string[] names = ManagedCandidates(p).Select(t => t.Name).Distinct().Take(MaxItems).ToArray();
            return names.Length == 0 ? "" : "; one of " + string.Join(", ", names);
        }

        private static Type ManagedType(SerializedProperty p, string name, out string error)
        {
            error = null;
            Type[] found = ManagedCandidates(p).Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (found.Length == 1)
            {
                return found[0];
            }

            error = found.Length == 0
                ? "no type " + name + " fits " + TypeLabel(p) + ManagedChoices(p)
                : name + " is ambiguous: " + string.Join(", ", found.Select(t => t.FullName));
            return null;
        }

        // `{a: 1, b: 2}` or `a: 1, b: 2`: each value is read by the field's own type.
        private static string ParseFields(SerializedProperty p, string text)
        {
            string body = text.Trim();
            if (body.StartsWith("{", StringComparison.Ordinal))
            {
                if (!body.EndsWith("}", StringComparison.Ordinal))
                {
                    return "missing }: " + text;
                }

                body = body.Substring(1, body.Length - 2);
            }

            var pairs = new List<KeyValuePair<string, string>>();
            foreach (string part in SplitTop(body))
            {
                Match match = _fieldStart.Match(part);
                if (match.Success)
                {
                    pairs.Add(new KeyValuePair<string, string>(match.Groups[1].Value, match.Groups[2].Value.Trim()));
                }
                else if (pairs.Count > 0)
                {
                    // A value that holds commas (`1, 2, 3`) was split by them: put it back together.
                    pairs[pairs.Count - 1] = new KeyValuePair<string, string>(pairs[pairs.Count - 1].Key, pairs[pairs.Count - 1].Value + ", " + part.Trim());
                }
                else if (part.Trim().Length > 0)
                {
                    return TypeLabel(p) + " takes `name: value` pairs, not " + part.Trim();
                }
            }

            List<SerializedProperty> children = Children(p).ToList();
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                SerializedProperty child = children.FirstOrDefault(c => string.Equals(c.name, pair.Key, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ShownName(c.name), pair.Key, StringComparison.OrdinalIgnoreCase));
                if (child == null)
                {
                    return TypeLabel(p) + " has no field " + pair.Key + "; fields: " + string.Join(", ", children.Select(c => ShownName(c.name)));
                }

                string problem = ParseElement(child, pair.Value);
                if (problem != null)
                {
                    return ShownName(child.name) + ": " + problem;
                }
            }

            return null;
        }

        // `[a, b, c]`, `a, b, c` or `empty`; vectors are `(1, 2, 3)` or, bare, in runs of their size.
        private static string ParseArray(SerializedProperty p, string text)
        {
            string body = text.Trim();
            if (body == "empty" || body == "[]")
            {
                p.arraySize = 0;
                return null;
            }

            if (body.StartsWith("[", StringComparison.Ordinal) && body.EndsWith("]", StringComparison.Ordinal))
            {
                body = body.Substring(1, body.Length - 2);
            }

            List<string> items = SplitTop(body).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            int arity = items.Count > 0 && !items.Any(s => s.StartsWith("(", StringComparison.Ordinal)) ? Arity(p) : 1;
            if (arity > 1 && items.Count % arity == 0 && items.All(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                items = Enumerable.Range(0, items.Count / arity).Select(i => string.Join(", ", items.Skip(i * arity).Take(arity))).ToList();
            }

            if (body.Length > 0 && items.Count == 0)
            {
                return "not a list like [a, b, c]: " + text;
            }

            p.arraySize = items.Count;
            for (int i = 0; i < items.Count; i++)
            {
                string problem = ParseElement(p.GetArrayElementAtIndex(i), items[i]);
                if (problem != null)
                {
                    return ShownName(p.name) + "[" + i.ToString(CultureInfo.InvariantCulture) + "]: " + problem;
                }
            }

            return null;
        }

        // How many bare numbers one element of the list takes: 1 when it is no vector.
        private static int Arity(SerializedProperty list)
        {
            switch (list.arrayElementType)
            {
                case "Vector2":
                case "Vector2Int":
                    return 2;
                case "Vector3":
                case "Vector3Int":
                case "Quaternion":
                    return 3;
                case "Vector4":
                case "Rect":
                case "ColorRGBA":
                case "Color":
                    return 4;
                default:
                    return 1;
            }
        }

        // One value inside a struct or list: a quoted string loses its quotes, the rest is read by its type.
        private static string ParseElement(SerializedProperty p, string text)
        {
            string trimmed = text.Trim();
            if (p.propertyType == SerializedPropertyType.String && trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Replace("\\n", "\n").Replace("\\\"", "\"");
            }

            return Parse(p, trimmed);
        }

        // Splits at commas outside (), [], {} and quotes.
        private static List<string> SplitTop(string text)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            bool quoted = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    current.Append(c).Append(text[++i]);
                    continue;
                }

                if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (!quoted && (c == '(' || c == '[' || c == '{'))
                {
                    depth++;
                }
                else if (!quoted && (c == ')' || c == ']' || c == '}'))
                {
                    depth--;
                }
                else if (!quoted && depth == 0 && c == ',')
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            parts.Add(current.ToString());
            return parts;
        }

        // `text` without the word it starts with (case-insensitive, whole word) and any separator after it.
        private static string Lead(string text, string word)
        {
            string t = text.Trim();
            if (t.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (t.Length == word.Length || !char.IsLetterOrDigit(t[word.Length])))
            {
                return t.Substring(word.Length).TrimStart(' ', ',', ';');
            }

            return t;
        }
    }
}
