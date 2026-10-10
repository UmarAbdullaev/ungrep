using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// The values `ug set` reads from text beyond numbers and references (SPEC-editor-ops, Endpoints):
    /// bounds, curves, gradients, hashes, whole structs, `[a, b]` lists and `SerializeReference` types.
    /// Each reads what <see cref="Values.Format"/> prints, so a value read can be set back; the text
    /// rules themselves are in Values.Text.cs, shared with `ug run`.
    /// </summary>
    internal static partial class Values
    {
        internal static string FormatCurve(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0)
            {
                return "curve empty";
            }

            Keyframe[] keys = curve.keys;
            return "curve " + string.Join(", ", keys.Take(MaxItems).Select(k => Number(k.time) + ":" + Number(k.value)))
                + (keys.Length > MaxItems ? ", ... " + (keys.Length - MaxItems).ToString(CultureInfo.InvariantCulture) + " more" : "");
        }

        internal static string FormatGradient(Gradient gradient)
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
            if (!TryBounds(text, out Bounds bounds))
            {
                return BoundsHelp + text;
            }

            p.boundsValue = bounds;
            return null;
        }

        private static string ParseCurve(SerializedProperty p, string text)
        {
            string problem = ReadCurve(text, out AnimationCurve curve);
            if (problem == null)
            {
                p.animationCurveValue = curve;
            }

            return problem;
        }

        private static string ParseGradient(SerializedProperty p, string text)
        {
            string problem = ReadGradient(text, out Gradient gradient);
            if (problem == null)
            {
                p.gradientValue = gradient;
            }

            return problem;
        }

        private static string ParseHash(SerializedProperty p, string text)
        {
            if (!TryHash(text, out Hash128 hash))
            {
                return HashHelp + text;
            }

            p.hash128Value = hash;
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
            List<KeyValuePair<string, string>> pairs = FieldPairs(text, out string error);
            if (pairs == null)
            {
                return error.StartsWith("missing", StringComparison.Ordinal) ? error : TypeLabel(p) + " " + error;
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
            List<string> items = ListItems(text, Arity(p.arrayElementType));
            if (items == null)
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

        // How many bare numbers one element of a list takes: 1 when it is no vector.
        internal static int Arity(string elementType)
        {
            switch (elementType)
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
                case "RectInt":
                case "ColorRGBA":
                case "Color":
                case "Color32":
                    return 4;
                default:
                    return 1;
            }
        }

        // One value inside a struct or list: a quoted string loses its quotes, the rest is read by its type.
        private static string ParseElement(SerializedProperty p, string text) =>
            Parse(p, p.propertyType == SerializedPropertyType.String ? Unquote(text) : text.Trim());
    }
}
