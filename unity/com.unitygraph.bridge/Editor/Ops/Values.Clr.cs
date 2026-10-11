using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `ug run` arguments (SPEC-editor-link, Running code): the text grammar of Values.Text.cs read
    /// into a parameter's System.Type. Each read also says how exactly the text fits the type, so an
    /// overload whose arguments fit best can be picked.
    /// </summary>
    internal static partial class Values
    {
        /// <summary>The text fits the type as written: a whole number for an integer, an enum name, an address.</summary>
        public const int Exact = 0;

        /// <summary>The text fits after a widening: a whole number for a float, an enum number, a static Parse.</summary>
        public const int Loose = 1;

        /// <summary>Any text fits: string and object parameters.</summary>
        public const int Loosest = 2;

        private static readonly Dictionary<Type, (decimal Min, decimal Max)> _wholeRanges = new Dictionary<Type, (decimal, decimal)>
        {
            { typeof(sbyte), (sbyte.MinValue, sbyte.MaxValue) }, { typeof(byte), (byte.MinValue, byte.MaxValue) },
            { typeof(short), (short.MinValue, short.MaxValue) }, { typeof(ushort), (ushort.MinValue, ushort.MaxValue) },
            { typeof(int), (int.MinValue, int.MaxValue) }, { typeof(uint), (uint.MinValue, uint.MaxValue) },
            { typeof(long), (long.MinValue, long.MaxValue) }, { typeof(ulong), (ulong.MinValue, ulong.MaxValue) },
        };

        private static readonly Type[] _listInterfaces =
        {
            typeof(List<>), typeof(IList<>), typeof(IReadOnlyList<>), typeof(IEnumerable<>), typeof(ICollection<>), typeof(IReadOnlyCollection<>),
        };

        /// <summary>
        /// Reads <paramref name="text"/> as a <paramref name="type"/>; null when it worked, else why not.
        /// <paramref name="rank"/> is <see cref="Exact"/>, <see cref="Loose"/> or <see cref="Loosest"/>.
        /// </summary>
        public static string ParseAs(Type type, string text, out object value, out int rank)
        {
            value = null;
            rank = Exact;
            text = text ?? "";
            string t = text.Trim();
            if (type.IsByRef)
            {
                type = type.GetElementType();
            }

            if (type == typeof(string) || type == typeof(object))
            {
                value = text;
                rank = Loosest;
                return null;
            }

            Type inner = Nullable.GetUnderlyingType(type);
            if (inner != null)
            {
                return t == "null" ? null : ParseAs(inner, text, out value, out rank);
            }

            if (type.IsEnum)
            {
                return ParseEnum(type, t, out value, out rank);
            }

            if (_wholeRanges.TryGetValue(type, out (decimal Min, decimal Max) range))
            {
                if (!decimal.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal whole))
                {
                    return "not a whole number: " + text;
                }

                if (whole < range.Min || whole > range.Max)
                {
                    return "out of range for " + ApiOps.TypeName(type) + ": " + text;
                }

                value = Convert.ChangeType(whole, type, CultureInfo.InvariantCulture);
                return null;
            }

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                rank = TryWhole(t, out _) ? Loose : Exact;
                if (type == typeof(decimal))
                {
                    bool fits = decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal exact);
                    value = exact;
                    return fits ? null : "not a number: " + text;
                }

                if (!TryNumber(t, out double number))
                {
                    return "not a number: " + text;
                }

                value = type == typeof(float) ? (object)(float)number : number;
                return null;
            }

            if (type == typeof(bool))
            {
                bool ok = TryBool(t, out bool flag);
                value = flag;
                rank = t == "true" || t == "false" ? Exact : Loose;
                return ok ? null : "not true or false: " + text;
            }

            if (type == typeof(char))
            {
                value = text.Length == 1 ? (object)text[0] : null;
                rank = Loose;
                return text.Length == 1 ? null : "not one character: " + text;
            }

            string unity = ParseUnityValue(type, t, out value);
            if (unity != NotUnity)
            {
                return unity;
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                if (t == "null")
                {
                    return null;
                }

                value = FindReference(t, type, out string error);
                return value == null ? error ?? "not found: " + t : null;
            }

            if (type == typeof(Type))
            {
                value = ApiOps.Resolve(t, out string ambiguous);
                return value != null ? null : ambiguous ?? "no type " + t;
            }

            Type element = ElementType(type);
            if (element != null)
            {
                return ParseList(type, element, t, out value, out rank);
            }

            MethodInfo parse = ParseMethod(type);
            if (parse != null)
            {
                rank = Loose;
                try
                {
                    value = parse.GetParameters().Length == 2 ? parse.Invoke(null, new object[] { t, CultureInfo.InvariantCulture }) : parse.Invoke(null, new object[] { t });
                    return null;
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    return "not a " + ApiOps.TypeName(type) + " (" + FirstLine(e.InnerException.Message) + "): " + text;
                }
            }

            return ParseMembers(type, t, out value);
        }

        /// <summary>The element type of an array or a list interface `ug run` fills from `[a, b]`; null for other types.</summary>
        public static Type ElementType(Type type)
        {
            if (type.IsArray)
            {
                return type.GetArrayRank() == 1 ? type.GetElementType() : null;
            }

            return type.IsGenericType && _listInterfaces.Contains(type.GetGenericTypeDefinition()) ? type.GetGenericArguments()[0] : null;
        }

        /// <summary>Items read as <paramref name="element"/> into an array or a List; the rank is the loosest item's.</summary>
        public static string FillList(Type type, Type element, IList<string> items, out object value, out int rank)
        {
            value = null;
            rank = Exact;
            Array array = Array.CreateInstance(element, items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                string item = element == typeof(string) || element == typeof(object) ? Unquote(items[i]) : items[i];
                string problem = ParseAs(element, item, out object one, out int itemRank);
                if (problem != null)
                {
                    return "[" + i.ToString(CultureInfo.InvariantCulture) + "]: " + problem;
                }

                array.SetValue(one, i);
                rank = Math.Max(rank, itemRank);
            }

            value = type.IsArray || type.GetGenericTypeDefinition() != typeof(List<>) && type.IsAssignableFrom(array.GetType())
                ? (object)array
                : Activator.CreateInstance(typeof(List<>).MakeGenericType(element), new object[] { array });
            return null;
        }

        private const string NotUnity = "\0";

        private static string ParseEnum(Type type, string text, out object value, out int rank)
        {
            value = null;
            rank = Exact;
            string[] names = Enum.GetNames(type);
            if (TryWhole(text, out long raw))
            {
                rank = Loose;
                value = Enum.ToObject(type, raw);
                return null;
            }

            string[] parts = text.Split(',', '|').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0 || (parts.Length > 1 && !type.IsDefined(typeof(FlagsAttribute), false)))
            {
                return "not one of " + string.Join(", ", names) + ": " + text;
            }

            long bits = 0;
            foreach (string part in parts)
            {
                int index = EnumIndex(names, null, part);
                if (index < 0)
                {
                    return "not one of " + string.Join(", ", names) + ": " + part;
                }

                bits |= Convert.ToInt64(Enum.Parse(type, names[index]), CultureInfo.InvariantCulture);
            }

            value = Enum.ToObject(type, bits);
            return null;
        }

        // Unity's value types, read as `ug set` reads them; NotUnity for other types.
        private static string ParseUnityValue(Type type, string text, out object value)
        {
            value = null;
            float[] n;
            if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4) || type == typeof(Quaternion) || type == typeof(Rect)
                || type == typeof(Vector2Int) || type == typeof(Vector3Int) || type == typeof(RectInt))
            {
                int count = Arity(type.Name);
                if (!TryNumbers(text, count, out n))
                {
                    return NumbersHelp(count, text);
                }

                value = type == typeof(Vector2) ? new Vector2(n[0], n[1])
                    : type == typeof(Vector3) ? new Vector3(n[0], n[1], n[2])
                    : type == typeof(Vector4) ? new Vector4(n[0], n[1], n[2], n[3])
                    : type == typeof(Quaternion) ? Quaternion.Euler(n[0], n[1], n[2])
                    : type == typeof(Rect) ? new Rect(n[0], n[1], n[2], n[3])
                    : type == typeof(Vector2Int) ? new Vector2Int(Mathf.RoundToInt(n[0]), Mathf.RoundToInt(n[1]))
                    : type == typeof(Vector3Int) ? new Vector3Int(Mathf.RoundToInt(n[0]), Mathf.RoundToInt(n[1]), Mathf.RoundToInt(n[2]))
                    : (object)new RectInt(Mathf.RoundToInt(n[0]), Mathf.RoundToInt(n[1]), Mathf.RoundToInt(n[2]), Mathf.RoundToInt(n[3]));
                return null;
            }

            if (type == typeof(Color) || type == typeof(Color32))
            {
                if (!TryColor(text, out Color color))
                {
                    return ColorHelp + text;
                }

                value = type == typeof(Color) ? (object)color : (Color32)color;
                return null;
            }

            if (type == typeof(Bounds))
            {
                bool ok = TryBounds(text, out Bounds bounds);
                value = bounds;
                return ok ? null : BoundsHelp + text;
            }

            if (type == typeof(Hash128))
            {
                bool ok = TryHash(text, out Hash128 hash);
                value = hash;
                return ok ? null : HashHelp + text;
            }

            if (type == typeof(LayerMask))
            {
                string problem = ReadLayerMask(text, out int mask);
                value = (LayerMask)mask;
                return problem;
            }

            if (type == typeof(AnimationCurve))
            {
                string problem = ReadCurve(text, out AnimationCurve curve);
                value = curve;
                return problem;
            }

            if (type == typeof(Gradient))
            {
                string problem = ReadGradient(text, out Gradient gradient);
                value = gradient;
                return problem;
            }

            return NotUnity;
        }

        private static string ParseList(Type type, Type element, string text, out object value, out int rank)
        {
            value = null;
            rank = Exact;
            List<string> items = ListItems(text, Arity(element.Name));
            return items == null ? "not a list like [a, b, c]: " + text : FillList(type, element, items, out value, out rank);
        }

        // A public static Parse(string, IFormatProvider) or Parse(string) that returns the type.
        private static MethodInfo ParseMethod(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            MethodInfo parse = type.GetMethod("Parse", flags, null, new[] { typeof(string), typeof(IFormatProvider) }, null)
                ?? type.GetMethod("Parse", flags, null, new[] { typeof(string) }, null);
            return parse != null && type.IsAssignableFrom(parse.ReturnType) ? parse : null;
        }

        // `{count: 3, offset: 1, 2, 3}`: a new instance with public fields and settable properties by name.
        private static string ParseMembers(Type type, string text, out object value)
        {
            value = null;
            string name = ApiOps.TypeName(type);
            bool creatable = type.IsValueType || (!type.IsAbstract && !type.IsInterface && !typeof(Delegate).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) != null);
            if (!creatable)
            {
                return "ug run cannot read a " + name + " from text (ug eval can build one)";
            }

            if (!type.IsValueType && text == "null")
            {
                return null;
            }

            List<KeyValuePair<string, string>> pairs = FieldPairs(text, out string error);
            if (pairs == null || pairs.Count == 0)
            {
                return name + " takes `{name: value}` pairs" + (error == null ? "" : ", " + error.Replace("takes `name: value` pairs, ", ""));
            }

            object made = Activator.CreateInstance(type);
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                MemberInfo member = (MemberInfo)type.GetField(pair.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?? type.GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                Type memberType = (member as FieldInfo)?.FieldType ?? (member as PropertyInfo)?.PropertyType;
                if (memberType == null || (member is PropertyInfo property && (!property.CanWrite || property.GetIndexParameters().Length > 0)))
                {
                    return name + " has no settable " + pair.Key;
                }

                string item = memberType == typeof(string) ? Unquote(pair.Value) : pair.Value;
                string problem = ParseAs(memberType, item, out object one, out _);
                if (problem != null)
                {
                    return pair.Key + ": " + problem;
                }

                if (member is FieldInfo field)
                {
                    field.SetValue(made, one);
                }
                else
                {
                    ((PropertyInfo)member).SetValue(made, one);
                }
            }

            value = made;
            return null;
        }

        private static string FirstLine(string text)
        {
            int end = (text ?? "").IndexOf('\n');
            return end < 0 ? text ?? "" : text.Substring(0, end).TrimEnd('\r');
        }
    }
}
