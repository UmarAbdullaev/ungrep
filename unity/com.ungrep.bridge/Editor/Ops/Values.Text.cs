using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Ungrep.Bridge
{
    /// <summary>
    /// The text grammar of `ug set` and `ug run` arguments (SPEC-editor-ops, Endpoints;
    /// SPEC-editor-link, Running code): text to a value, with no property or parameter in sight.
    /// <see cref="Values.Parse"/> writes what these read into a SerializedProperty and
    /// <see cref="Values.ParseAs"/> into a parameter of a System.Type.
    /// </summary>
    internal static partial class Values
    {
        public const string CurveHelp = "not a curve: keys time:value like `0:0, 1:1`, optionally led by linear, constant or smooth; one number is a flat curve: ";
        public const string GradientHelp = "not a gradient: stops `#ff0000@0, #0000ff@1` (color@time, time 0 to 1; without @ they are spread evenly), optionally led by fixed: ";
        public const string BoundsHelp = "not bounds (`center 0, 1, 0 size 2, 2, 2` or six numbers: center then size): ";
        public const string ColorHelp = "not a color (#ff8800, #ff8800cc or 1, 0.5, 0, 1): ";
        public const string HashHelp = "not a hash (32 hex digits): ";

        private static readonly Regex _fieldStart = new Regex(@"^\s*([A-Za-z_]\w*)\s*:(.*)$", RegexOptions.Singleline);

        public static bool TryWhole(string text, out long value) =>
            long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        public static bool TryNumber(string text, out double value) =>
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        /// <summary>`true`, `false`, `1` or `0`.</summary>
        public static bool TryBool(string text, out bool value)
        {
            string t = text.Trim();
            value = t == "true" || t == "1";
            return value || t == "false" || t == "0";
        }

        /// <summary>The index of an enum name (or display name), case-insensitive; -1 when none matches.</summary>
        public static int EnumIndex(string[] names, string[] displayNames, string text)
        {
            string t = text.Trim();
            int index = Array.FindIndex(names, n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
            return index >= 0 || displayNames == null ? index : Array.FindIndex(displayNames, n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase));
        }

        public static bool TryNumbers(string text, int count, out float[] numbers)
        {
            string[] parts = text.Trim('(', ')', ' ').Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            numbers = new float[parts.Length];
            if (parts.Length != count)
            {
                return false;
            }

            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Why <paramref name="text"/> is not <paramref name="count"/> numbers.</summary>
        public static string NumbersHelp(int count, string text) =>
            "not " + count.ToString(CultureInfo.InvariantCulture) + " numbers like "
            + string.Join(", ", Enumerable.Range(1, count).Select(i => i.ToString(CultureInfo.InvariantCulture))) + ": " + text;

        public static bool TryColor(string text, out Color color)
        {
            text = text.Trim();
            if (text.StartsWith("#", StringComparison.Ordinal))
            {
                return ColorUtility.TryParseHtmlString(text, out color);
            }

            if (TryNumbers(text, 4, out float[] rgba))
            {
                color = new Color(rgba[0], rgba[1], rgba[2], rgba[3]);
                return true;
            }

            if (TryNumbers(text, 3, out float[] rgb))
            {
                color = new Color(rgb[0], rgb[1], rgb[2], 1);
                return true;
            }

            return ColorUtility.TryParseHtmlString(text, out color);
        }

        /// <summary>`center 0, 1, 0 size 2, 2, 2` or six numbers.</summary>
        public static bool TryBounds(string text, out Bounds bounds)
        {
            string numbers = text.Replace("center", " ").Replace("size", ",").Replace("(", " ").Replace(")", " ");
            bool ok = TryNumbers(numbers, 6, out float[] n);
            bounds = ok ? new Bounds(new Vector3(n[0], n[1], n[2]), new Vector3(n[3], n[4], n[5])) : default;
            return ok;
        }

        public static bool TryHash(string text, out Hash128 hash)
        {
            string t = text.Trim();
            bool ok = t.Length == 32 && t.All(Uri.IsHexDigit);
            hash = ok ? Hash128.Parse(t) : default;
            return ok;
        }

        /// <summary>A curve from `curve linear 0:0, 1:1`, `0.5` (flat) or `empty`; null when it worked, else why not.</summary>
        public static string ReadCurve(string text, out AnimationCurve curve)
        {
            curve = null;
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
                curve = new AnimationCurve();
                return null;
            }

            if (float.TryParse(body, NumberStyles.Float, CultureInfo.InvariantCulture, out float flat))
            {
                curve = AnimationCurve.Constant(0f, 1f, flat);
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

            curve = new AnimationCurve(keys.ToArray());
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

            return null;
        }

        /// <summary>A gradient from `gradient fixed #ff0000@0, #0000ff@1`; null when it worked, else why not.</summary>
        public static string ReadGradient(string text, out Gradient gradient)
        {
            gradient = null;
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
            if (stops.Length == 0 || stops.Length > 8)
            {
                return stops.Length == 0 ? GradientHelp + text : "a gradient has at most 8 stops: " + text;
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
                    return GradientHelp + text;
                }

                colors.Add(new GradientColorKey(color, time));
                alphas.Add(new GradientAlphaKey(color.a, time));
            }

            gradient = new Gradient { mode = mode };
            gradient.SetKeys(colors.OrderBy(k => k.time).ToArray(), alphas.OrderBy(k => k.time).ToArray());
            return null;
        }

        /// <summary>A layer mask from bits, `Nothing`, `Everything` or layer names; null when it worked, else why not.</summary>
        public static string ReadLayerMask(string text, out int mask)
        {
            string t = text.Trim();
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out mask))
            {
                return null;
            }

            mask = t == "Everything" ? -1 : 0;
            if (t == "Nothing" || t == "Everything")
            {
                return null;
            }

            foreach (string name in t.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                int layer = LayerMask.NameToLayer(name);
                if (layer < 0)
                {
                    return "no layer " + name;
                }

                mask |= 1 << layer;
            }

            return null;
        }

        /// <summary>
        /// The items of `[a, b, c]`, `a, b, c`, `empty` or `[]`; bare numbers are grouped in runs of
        /// <paramref name="arity"/> (vectors). Null when the text is no list.
        /// </summary>
        public static List<string> ListItems(string text, int arity)
        {
            string body = text.Trim();
            if (body == "empty" || body == "[]")
            {
                return new List<string>();
            }

            if (body.StartsWith("[", StringComparison.Ordinal) && body.EndsWith("]", StringComparison.Ordinal))
            {
                body = body.Substring(1, body.Length - 2);
            }

            List<string> items = SplitTop(body).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (items.Any(s => s.StartsWith("(", StringComparison.Ordinal)))
            {
                arity = 1;
            }

            if (arity > 1 && items.Count % arity == 0 && items.All(s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            {
                items = Enumerable.Range(0, items.Count / arity).Select(i => string.Join(", ", items.Skip(i * arity).Take(arity))).ToList();
            }

            return body.Length > 0 && items.Count == 0 ? null : items;
        }

        /// <summary>
        /// The `name: value` pairs of `{a: 1, b: 1, 2, 3}` or `a: 1`; a value holding commas is put
        /// back together. Null with <paramref name="error"/> naming the first part that is no pair.
        /// </summary>
        public static List<KeyValuePair<string, string>> FieldPairs(string text, out string error)
        {
            error = null;
            string body = text.Trim();
            if (body.StartsWith("{", StringComparison.Ordinal))
            {
                if (!body.EndsWith("}", StringComparison.Ordinal))
                {
                    error = "missing }: " + text;
                    return null;
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
                    pairs[pairs.Count - 1] = new KeyValuePair<string, string>(pairs[pairs.Count - 1].Key, pairs[pairs.Count - 1].Value + ", " + part.Trim());
                }
                else if (part.Trim().Length > 0)
                {
                    error = "takes `name: value` pairs, not " + part.Trim();
                    return null;
                }
            }

            return pairs;
        }

        /// <summary>A string item inside a list or struct: `"a, b"` loses its quotes and escapes.</summary>
        public static string Unquote(string text)
        {
            string t = text.Trim();
            return t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"'
                ? t.Substring(1, t.Length - 2).Replace("\\n", "\n").Replace("\\\"", "\"")
                : t;
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
