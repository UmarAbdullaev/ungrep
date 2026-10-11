using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// What `ug run` and `ug eval` return, as one line (SPEC-editor-link, Running code, Answer
    /// shape): Unity values as `ug inspect` prints them, objects as addresses, collections and
    /// objects capped. <see cref="Caps.Compact"/> is the answer; when it cut something,
    /// <see cref="Caps.Full"/> is what goes to the result file.
    /// </summary>
    internal static partial class Values
    {
        internal sealed class Caps
        {
            public int Items;
            public int Depth;
            public int Text;
            public int Members;

            public static readonly Caps Compact = new Caps { Items = MaxItems, Depth = 1, Text = 200, Members = 20 };
            public static readonly Caps Full = new Caps { Items = 1000, Depth = 2, Text = int.MaxValue, Members = 200 };
        }

        // Enumerating past this only to count what was left out stops here.
        private const int CountLimit = 100000;

        /// <summary>A value as one line; <paramref name="cut"/> says whether the caps left anything out.</summary>
        public static string Show(object value, Caps caps, out bool cut)
        {
            var state = new ShowState { Caps = caps };
            string text = Show(value, caps.Depth, state);
            cut = state.Cut;
            return text;
        }

        private sealed class ShowState
        {
            public Caps Caps;
            public bool Cut;
        }

        private static string Show(object value, int depth, ShowState state)
        {
            if (value == null)
            {
                return "null";
            }

            if (value is UnityEngine.Object unity)
            {
                return unity == null ? "missing (destroyed " + value.GetType().Name + ")" : ShowObject(unity);
            }

            switch (value)
            {
                case string text:
                    return Clip(text, state, true);
                case char c:
                    return Quote(c.ToString());
                case bool flag:
                    return flag ? "true" : "false";
                case float single:
                    return Number(single);
                case double number:
                    return Number(number);
                case Enum e:
                    return e.ToString();
                case Type type:
                    return (type.Namespace == null ? "" : type.Namespace + ".") + ApiOps.TypeName(type);
                case Delegate call:
                    return "delegate " + (call.Method.DeclaringType == null ? "" : ApiOps.TypeName(call.Method.DeclaringType) + ".") + call.Method.Name;
            }

            string unityValue = ShowUnityValue(value);
            if (unityValue != null)
            {
                return unityValue;
            }

            Type valueType = value.GetType();
            if (valueType.IsPrimitive || value is decimal)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            if (IsTuple(valueType))
            {
                return "(" + string.Join(", ", TupleItems(value).Select(i => Show(i, depth, state))) + ")";
            }

            if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                return Show(valueType.GetProperty("Key").GetValue(value), depth, state) + ": " + Show(valueType.GetProperty("Value").GetValue(value), depth, state);
            }

            if (value is IFormattable formattable)
            {
                return Clip(formattable.ToString(null, CultureInfo.InvariantCulture), state, false);
            }

            if (value is IConvertible convertible)
            {
                return Clip(convertible.ToString(CultureInfo.InvariantCulture), state, false);
            }

            // A collection that prints itself (a JSON token) prints its own text, on one line.
            if (value is IEnumerable && OverridesToString(valueType))
            {
                return Clip(OneLine(value.ToString()), state, false);
            }

            if (value is IDictionary dictionary)
            {
                return ShowDictionary(dictionary, depth, state);
            }

            if (value is IEnumerable items)
            {
                return ShowItems(items, depth, state);
            }

            return ShowMembers(value, depth, state);
        }

        // A component as `address:Component`, the form `ug set` takes; anything else as a reference.
        private static string ShowObject(UnityEngine.Object value) =>
            value is Component component
                ? ComponentReference(component)
                : Reference(value, value.GetInstanceID());

        // Unity's value types as `ug inspect` prints them; null for other types.
        private static string ShowUnityValue(object value)
        {
            switch (value)
            {
                case Vector2 v:
                    return Numbers(v.x, v.y);
                case Vector3 v:
                    return Numbers(v.x, v.y, v.z);
                case Vector4 v:
                    return Numbers(v.x, v.y, v.z, v.w);
                case Vector2Int v:
                    return Whole(v.x, v.y);
                case Vector3Int v:
                    return Whole(v.x, v.y, v.z);
                case Quaternion q:
                    Vector3 euler = q.eulerAngles;
                    return Numbers(Round(euler.x), Round(euler.y), Round(euler.z));
                case Rect r:
                    return Numbers(r.x, r.y, r.width, r.height);
                case RectInt r:
                    return Whole(r.x, r.y, r.width, r.height);
                case Color c:
                    return Numbers(c.r, c.g, c.b, c.a);
                case Color32 c:
                    return "#" + ColorUtility.ToHtmlStringRGBA(c).ToLowerInvariant();
                case Bounds b:
                    return "center " + Numbers(b.center.x, b.center.y, b.center.z) + " size " + Numbers(b.size.x, b.size.y, b.size.z);
                case LayerMask mask:
                    return LayerMaskText(mask.value);
                case AnimationCurve curve:
                    return FormatCurve(curve);
                case Gradient gradient:
                    return FormatGradient(gradient);
                case Hash128 hash:
                    return hash.ToString();
                default:
                    return null;
            }
        }

        private static string ShowDictionary(IDictionary dictionary, int depth, ShowState state)
        {
            string head = ApiOps.TypeName(dictionary.GetType()) + "[" + dictionary.Count.ToString(CultureInfo.InvariantCulture) + "]";
            if (dictionary.Count == 0)
            {
                return head + " {}";
            }

            if (depth <= 0)
            {
                state.Cut = true;
                return head;
            }

            var parts = new List<string>();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (parts.Count == state.Caps.Items)
                {
                    break;
                }

                parts.Add(Show(entry.Key, depth - 1, state) + ": " + Show(entry.Value, depth - 1, state));
            }

            return head + " {" + string.Join(", ", parts) + More(dictionary.Count - parts.Count, state) + "}";
        }

        private static string ShowItems(IEnumerable items, int depth, ShowState state)
        {
            var parts = new List<string>();
            int count = 0;
            IEnumerator it = items.GetEnumerator();
            try
            {
                while (count < CountLimit && it.MoveNext())
                {
                    if (count++ < state.Caps.Items && depth > 0)
                    {
                        parts.Add(Show(it.Current, depth - 1, state));
                    }
                }
            }
            finally
            {
                (it as IDisposable)?.Dispose();
            }

            Type type = items.GetType();
            string head = ApiOps.TypeName(type.IsArray ? type.GetElementType() : type) + "[" + count.ToString(CultureInfo.InvariantCulture) + (count == CountLimit ? "+" : "") + "]";
            if (count == 0)
            {
                return head + " []";
            }

            if (depth <= 0)
            {
                state.Cut = true;
                return head;
            }

            return head + " [" + string.Join(", ", parts) + More(count - parts.Count, state) + "]";
        }

        // `TypeName {field: value, prop: value}`: public instance fields and properties.
        private static string ShowMembers(object value, int depth, ShowState state)
        {
            Type type = value.GetType();
            string name = ApiOps.TypeName(type);
            if (depth <= 0)
            {
                state.Cut = true;
                return OverridesToString(type) ? Clip(OneLine(value.ToString()), state, false) : name;
            }

            var members = new List<MemberInfo>();
            members.AddRange(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
            members.AddRange(type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(ObsoleteAttribute), true)));
            if (members.Count == 0)
            {
                return OverridesToString(type) ? Clip(OneLine(value.ToString()), state, false) : name + " {}";
            }

            var parts = new List<string>();
            foreach (MemberInfo member in members.Take(state.Caps.Members))
            {
                string shown;
                try
                {
                    object held = member is FieldInfo field ? field.GetValue(value) : ((PropertyInfo)member).GetValue(value);
                    shown = Show(held, depth - 1, state);
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    shown = "<threw " + inner.GetType().Name + ">";
                }

                parts.Add(member.Name + ": " + shown);
            }

            return name + " {" + string.Join(", ", parts) + More(members.Count - parts.Count, state) + "}";
        }

        private static string More(int left, ShowState state)
        {
            if (left <= 0)
            {
                return "";
            }

            state.Cut = true;
            return ", ... +" + left.ToString(CultureInfo.InvariantCulture) + " more";
        }

        // A string quoted (or text as it is) and cut at the caps' length.
        private static string Clip(string text, ShowState state, bool quoted)
        {
            string kept = text;
            string tail = "";
            if (text.Length > state.Caps.Text)
            {
                state.Cut = true;
                kept = text.Substring(0, state.Caps.Text);
                tail = "... +" + (text.Length - state.Caps.Text).ToString(CultureInfo.InvariantCulture) + " chars";
            }

            return (quoted ? Quote(kept) : kept.Replace("\n", "\\n")) + tail;
        }

        // Multi-line text (indented JSON) on one line: each line break and the indent after it become one space.
        private static string OneLine(string text)
        {
            string[] lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            return string.Join(" ", lines.Select((l, i) => i == 0 ? l.TrimEnd() : l.Trim()).Where(l => l.Length > 0));
        }

        private static bool OverridesToString(Type type)
        {
            MethodInfo method = type.GetMethod("ToString", Type.EmptyTypes);
            return method != null && method.DeclaringType != typeof(object) && method.DeclaringType != typeof(ValueType);
        }

        private static bool IsTuple(Type type)
        {
            string name = type.IsGenericType ? type.GetGenericTypeDefinition().FullName : null;
            return name != null && (name.StartsWith("System.ValueTuple`", StringComparison.Ordinal) || name.StartsWith("System.Tuple`", StringComparison.Ordinal));
        }

        private static IEnumerable<object> TupleItems(object tuple)
        {
            Type type = tuple.GetType();
            for (int i = 1; i <= 7; i++)
            {
                FieldInfo field = type.GetField("Item" + i.ToString(CultureInfo.InvariantCulture));
                PropertyInfo property = field == null ? type.GetProperty("Item" + i.ToString(CultureInfo.InvariantCulture)) : null;
                if (field == null && property == null)
                {
                    yield break;
                }

                yield return field != null ? field.GetValue(tuple) : property.GetValue(tuple);
            }
        }

        private static string Whole(params int[] values) => string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
    }
}
