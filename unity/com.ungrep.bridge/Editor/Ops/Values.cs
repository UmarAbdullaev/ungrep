using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Ungrep.Bridge
{
    /// <summary>
    /// Serialized values as ug shows them and as `ug set` reads them (SPEC-editor-ops, Endpoints):
    /// numbers in the invariant culture, vectors `1, 2, 3`, colors `#rrggbb` or `r, g, b, a`,
    /// quaternions as euler angles, object references as addresses or asset paths.
    /// </summary>
    internal static partial class Values
    {
        public const int MaxItems = 10;

        private static readonly Dictionary<string, string> _gameObjectFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "name", "m_Name" },
            { "tag", "m_TagString" },
            { "layer", "m_Layer" },
            { "active", "m_IsActive" },
            { "static", "m_StaticEditorFlags" },
        };

        private static readonly Dictionary<string, string> _transformFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "position", "m_LocalPosition" },
            { "rotation", "m_LocalRotation" },
            { "scale", "m_LocalScale" },
        };

        /// <summary>`m_LocalPosition` shows as `localPosition`; `ug set` takes either.</summary>
        public static string ShownName(string name)
        {
            if (name.StartsWith("m_", StringComparison.Ordinal) && name.Length > 2)
            {
                return char.ToLowerInvariant(name[2]) + name.Substring(3);
            }

            return name;
        }

        /// <summary>The visible top-level fields of an object, without the script reference.</summary>
        public static IEnumerable<SerializedProperty> Fields(SerializedObject serialized)
        {
            SerializedProperty it = serialized.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                if (it.name == "m_Script")
                {
                    continue;
                }

                yield return it.Copy();
            }
        }

        public static SerializedProperty Find(SerializedObject serialized, string field, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(field))
            {
                error = "no field given";
                return null;
            }

            field = field.Trim();
            Dictionary<string, string> aliases = serialized.targetObject is GameObject ? _gameObjectFields
                : serialized.targetObject is Transform ? _transformFields
                : null;
            if (aliases != null && aliases.TryGetValue(field, out string alias))
            {
                field = alias;
            }

            string path = PropertyPath(field);
            SerializedProperty found = serialized.FindProperty(path);
            if (found == null && !path.StartsWith("m_", StringComparison.Ordinal))
            {
                found = serialized.FindProperty("m_" + char.ToUpperInvariant(path[0]) + path.Substring(1));
            }

            if (found == null && path.IndexOf('.') < 0)
            {
                string wanted = field.Replace(" ", "");
                found = Fields(serialized).FirstOrDefault(p =>
                    string.Equals(p.name, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ShownName(p.name), wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.displayName.Replace(" ", ""), wanted, StringComparison.OrdinalIgnoreCase));
            }

            if (found == null)
            {
                List<string> names = Fields(serialized).Select(p => ShownName(p.name)).ToList();
                error = "no field " + field + " on " + TargetName(serialized.targetObject)
                    + (names.Count == 0 ? "" : " (fields: " + string.Join(", ", names) + ")");
            }

            return found;
        }

        /// <summary>`waves[2].count` to Unity's `waves.Array.data[2].count`, `guns.size` to `guns.Array.size`.</summary>
        public static string PropertyPath(string field)
        {
            var path = new System.Text.StringBuilder();
            string[] parts = field.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (i > 0)
                {
                    path.Append('.');
                }

                if (part == "size" && i == parts.Length - 1 && i > 0)
                {
                    path.Append("Array.size");
                    continue;
                }

                int bracket = part.IndexOf('[');
                if (bracket > 0 && part.EndsWith("]", StringComparison.Ordinal))
                {
                    path.Append(part, 0, bracket).Append(".Array.data").Append(part, bracket, part.Length - bracket);
                }
                else
                {
                    path.Append(part);
                }
            }

            return path.ToString();
        }

        public static string TypeLabel(SerializedProperty p)
        {
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                return CleanType(p.arrayElementType) + "[" + p.arraySize.ToString(CultureInfo.InvariantCulture) + "]";
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.ExposedReference:
                    return CleanType(p.type);
                case SerializedPropertyType.Enum:
                    return "enum";
                case SerializedPropertyType.Color:
                    return "Color";
                case SerializedPropertyType.Quaternion:
                    return "rotation";
                case SerializedPropertyType.ManagedReference:
                    string full = p.managedReferenceFullTypename;
                    return string.IsNullOrEmpty(full) ? "reference" : full.Substring(full.LastIndexOfAny(new[] { ' ', '.', '/' }) + 1);
                default:
                    return CleanType(p.type);
            }
        }

        /// <summary>A value as `ug inspect` lists it: 10 items of a list or curve, structs one level deep.</summary>
        public static string Format(SerializedProperty p, int depth = 1) => Format(p, depth, MaxItems, new Cuts());

        /// <summary>As <see cref="Format(SerializedProperty, int)"/>; <paramref name="cut"/> says whether it left anything out.</summary>
        public static string Format(SerializedProperty p, out bool cut)
        {
            var cuts = new Cuts();
            string text = Format(p, 1, MaxItems, cuts);
            cut = cuts.Any;
            return text;
        }

        /// <summary>
        /// A value whole (`ug inspect --field`, SPEC-editor-ops C1): every item and key, structs to any
        /// depth; a cycle of `SerializeReference` objects stops at <see cref="WholeDepth"/> levels.
        /// </summary>
        public static string Whole(SerializedProperty p) => Format(p, WholeDepth, int.MaxValue, new Cuts());

        public const int WholeDepth = 32;

        // Whether a Format call left items or levels out.
        private sealed class Cuts
        {
            public bool Any;
        }

        private static string Format(SerializedProperty p, int depth, int maxItems, Cuts cuts)
        {
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                int size = p.arraySize;
                if (size == 0)
                {
                    return "empty";
                }

                var items = new List<string>();
                for (int i = 0; i < Math.Min(size, maxItems); i++)
                {
                    items.Add(Format(p.GetArrayElementAtIndex(i), depth, maxItems, cuts));
                }

                cuts.Any |= size > maxItems;
                return string.Join(", ", items) + (size > maxItems ? ", ... " + (size - maxItems).ToString(CultureInfo.InvariantCulture) + " more" : "");
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                    return p.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return p.boolValue ? "true" : "false";
                case SerializedPropertyType.Float:
                    return p.type == "double" ? Number(p.doubleValue) : Number(p.floatValue);
                case SerializedPropertyType.String:
                    return Quote(p.stringValue);
                case SerializedPropertyType.Character:
                    return Quote(((char)p.intValue).ToString());
                case SerializedPropertyType.Color:
                    return Numbers(p.colorValue.r, p.colorValue.g, p.colorValue.b, p.colorValue.a);
                case SerializedPropertyType.ObjectReference:
                    // A field typed plain Object (a UnityEvent target) names which component it holds.
                    return p.objectReferenceValue is Component held && ReferenceType(p) == typeof(UnityEngine.Object)
                        ? ComponentReference(held)
                        : Reference(p.objectReferenceValue, p.objectReferenceInstanceIDValue);
                case SerializedPropertyType.ExposedReference:
                    return Reference(p.exposedReferenceValue, 0);
                case SerializedPropertyType.LayerMask:
                    return LayerMaskText(p.intValue);
                case SerializedPropertyType.Enum:
                    int index = p.enumValueIndex;
                    return index >= 0 && index < p.enumNames.Length ? p.enumNames[index] : p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2:
                    return Numbers(p.vector2Value.x, p.vector2Value.y);
                case SerializedPropertyType.Vector3:
                    return Numbers(p.vector3Value.x, p.vector3Value.y, p.vector3Value.z);
                case SerializedPropertyType.Vector4:
                    return Numbers(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
                case SerializedPropertyType.Quaternion:
                    Vector3 euler = p.quaternionValue.eulerAngles;
                    return Numbers(Round(euler.x), Round(euler.y), Round(euler.z));
                case SerializedPropertyType.Rect:
                    return Numbers(p.rectValue.x, p.rectValue.y, p.rectValue.width, p.rectValue.height);
                case SerializedPropertyType.Bounds:
                    return "center " + Numbers(p.boundsValue.center.x, p.boundsValue.center.y, p.boundsValue.center.z)
                        + " size " + Numbers(p.boundsValue.size.x, p.boundsValue.size.y, p.boundsValue.size.z);
                case SerializedPropertyType.Vector2Int:
                    return p.vector2IntValue.x.ToString(CultureInfo.InvariantCulture) + ", " + p.vector2IntValue.y.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector3Int:
                    return p.vector3IntValue.x.ToString(CultureInfo.InvariantCulture) + ", " + p.vector3IntValue.y.ToString(CultureInfo.InvariantCulture)
                        + ", " + p.vector3IntValue.z.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.AnimationCurve:
                    AnimationCurve curve = p.animationCurveValue;
                    cuts.Any |= curve != null && curve.length > maxItems;
                    return FormatCurve(curve, maxItems);
                case SerializedPropertyType.Gradient:
                    Gradient gradient = p.gradientValue;
                    cuts.Any |= gradient != null && GradientTimes(gradient).Count > maxItems;
                    return FormatGradient(gradient, maxItems);
                case SerializedPropertyType.Hash128:
                    return p.hash128Value.ToString();
                case SerializedPropertyType.ManagedReference:
                case SerializedPropertyType.Generic:
                    if (p.propertyType == SerializedPropertyType.ManagedReference && string.IsNullOrEmpty(p.managedReferenceFullTypename))
                    {
                        return "null";
                    }

                    if (depth <= 0)
                    {
                        cuts.Any = true;
                        return "{...}";
                    }

                    return "{" + string.Join(", ", Children(p).Select(c => ShownName(c.name) + ": " + Format(c, depth - 1, maxItems, cuts))) + "}";
                default:
                    return p.propertyType.ToString();
            }
        }

        /// <summary>Sets a property from text; null when it worked, else why not. The caller applies the change.</summary>
        public static string Parse(SerializedProperty p, string text)
        {
            text = text ?? "";
            string trimmed = text.Trim();
            if (p.isArray && p.propertyType != SerializedPropertyType.String)
            {
                return ParseArray(p, trimmed);
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.ArraySize:
                    if (!TryWhole(trimmed, out long whole))
                    {
                        return "not a whole number: " + text;
                    }

                    if (p.name == "m_Layer" && p.serializedObject.targetObject is GameObject)
                    {
                        return whole >= 0 && whole < 32 ? Assign(() => p.intValue = (int)whole) : "layers are 0 to 31";
                    }

                    p.longValue = whole;
                    return null;
                case SerializedPropertyType.Boolean:
                    if (!TryBool(trimmed, out bool flag))
                    {
                        return "not true or false: " + text;
                    }

                    p.boolValue = flag;
                    return null;
                case SerializedPropertyType.Float:
                    if (!TryNumber(trimmed, out double number))
                    {
                        return "not a number: " + text;
                    }

                    if (p.type == "double")
                    {
                        p.doubleValue = number;
                    }
                    else
                    {
                        p.floatValue = (float)number;
                    }

                    return null;
                case SerializedPropertyType.String:
                    p.stringValue = text;
                    return null;
                case SerializedPropertyType.Character:
                    if (text.Length != 1)
                    {
                        return "not one character: " + text;
                    }

                    p.intValue = text[0];
                    return null;
                case SerializedPropertyType.Enum:
                    int index = EnumIndex(p.enumNames, p.enumDisplayNames, trimmed);
                    if (index < 0 && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw))
                    {
                        p.intValue = raw;
                        return null;
                    }

                    if (index < 0)
                    {
                        return "not one of " + string.Join(", ", p.enumNames);
                    }

                    p.enumValueIndex = index;
                    return null;
                case SerializedPropertyType.LayerMask:
                    string maskProblem = ReadLayerMask(trimmed, out int mask);
                    if (maskProblem == null)
                    {
                        p.intValue = mask;
                    }

                    return maskProblem;
                case SerializedPropertyType.Color:
                    if (!TryColor(trimmed, out Color color))
                    {
                        return ColorHelp + text;
                    }

                    p.colorValue = color;
                    return null;
                case SerializedPropertyType.Vector2:
                    return WithNumbers(trimmed, 2, n => p.vector2Value = new Vector2(n[0], n[1]));
                case SerializedPropertyType.Vector3:
                    return WithNumbers(trimmed, 3, n => p.vector3Value = new Vector3(n[0], n[1], n[2]));
                case SerializedPropertyType.Vector4:
                    return WithNumbers(trimmed, 4, n => p.vector4Value = new Vector4(n[0], n[1], n[2], n[3]));
                case SerializedPropertyType.Quaternion:
                    return WithNumbers(trimmed, 3, n => p.quaternionValue = Quaternion.Euler(n[0], n[1], n[2]));
                case SerializedPropertyType.Rect:
                    return WithNumbers(trimmed, 4, n => p.rectValue = new Rect(n[0], n[1], n[2], n[3]));
                case SerializedPropertyType.Vector2Int:
                    return WithNumbers(trimmed, 2, n => p.vector2IntValue = new Vector2Int(Mathf.RoundToInt(n[0]), Mathf.RoundToInt(n[1])));
                case SerializedPropertyType.Vector3Int:
                    return WithNumbers(trimmed, 3, n => p.vector3IntValue = new Vector3Int(Mathf.RoundToInt(n[0]), Mathf.RoundToInt(n[1]), Mathf.RoundToInt(n[2])));
                case SerializedPropertyType.ObjectReference:
                    return ParseReference(p, trimmed);
                case SerializedPropertyType.Bounds:
                    return ParseBounds(p, trimmed);
                case SerializedPropertyType.AnimationCurve:
                    return ParseCurve(p, trimmed);
                case SerializedPropertyType.Gradient:
                    return ParseGradient(p, trimmed);
                case SerializedPropertyType.Hash128:
                    return ParseHash(p, trimmed);
                case SerializedPropertyType.ManagedReference:
                    return ParseManaged(p, trimmed);
                case SerializedPropertyType.Generic:
                    return ParseFields(p, trimmed);
                default:
                    return "ug set cannot set a " + TypeLabel(p) + " from text";
            }
        }

        /// <summary>A reference as an address (scene objects), `asset path` or `asset path:sub asset`.</summary>
        public static string Reference(UnityEngine.Object value, int instanceId)
        {
            if (value == null)
            {
                return instanceId != 0 ? "missing" : "null";
            }

            GameObject owner = value as GameObject ?? (value as Component)?.gameObject;
            if (owner != null && Addressed(owner))
            {
                return Addresses.Of(owner);
            }

            string path = AssetDatabase.GetAssetPath(value);
            if (string.IsNullOrEmpty(path))
            {
                return value.name + " (not saved)";
            }

            if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return "builtin " + value.name;
            }

            return AssetDatabase.IsMainAsset(value) ? path : path + ":" + value.name;
        }

        /// <summary>A component as the address `ug set` takes (`Canvas:UiHandler`); its object as <see cref="Reference"/> names it otherwise.</summary>
        public static string ComponentReference(Component component) =>
            Addressed(component.gameObject) ? Addresses.Of(component) : Reference(component.gameObject, 0) + ":" + Addresses.ComponentName(component);

        // Scene objects and prefab objects print as addresses; other assets' objects by asset path.
        private static bool Addressed(GameObject owner) =>
            !EditorUtility.IsPersistent(owner) || AssetDatabase.GetAssetPath(owner).EndsWith(".prefab", StringComparison.Ordinal);

        public static string Number(float value) => value.ToString("G7", CultureInfo.InvariantCulture);

        public static string Number(double value) => value.ToString("G15", CultureInfo.InvariantCulture);

        public static string Numbers(params float[] values) => string.Join(", ", values.Select(Number));

        /// <summary>A short name for what a serialized object is: an address, an asset path or a type.</summary>
        public static string TargetName(UnityEngine.Object target)
        {
            if (target is Component component)
            {
                return Addresses.Of(component.gameObject) + " " + component.GetType().Name;
            }

            if (target is GameObject go)
            {
                return Addresses.Of(go);
            }

            string path = AssetDatabase.GetAssetPath(target);
            return string.IsNullOrEmpty(path) ? target.GetType().Name : path;
        }

        /// <summary>The Unity object type named by a serialized reference type (`PPtr<$Transform>`), or Object.</summary>
        public static Type ReferenceType(SerializedProperty p)
        {
            string name = CleanType(p.type);
            if (p.serializedObject.targetObject != null)
            {
                FieldInfo field = FieldOf(p);
                if (field != null)
                {
                    Type type = field.FieldType.IsArray ? field.FieldType.GetElementType()
                        : field.FieldType.IsGenericType ? field.FieldType.GetGenericArguments()[0]
                        : field.FieldType;
                    if (typeof(UnityEngine.Object).IsAssignableFrom(type))
                    {
                        return type;
                    }
                }
            }

            Type found = TypeCache.GetTypesDerivedFrom<UnityEngine.Object>().FirstOrDefault(t => t.Name == name);
            return found ?? typeof(UnityEngine.Object);
        }

        private static IEnumerable<SerializedProperty> Children(SerializedProperty p)
        {
            SerializedProperty child = p.Copy();
            SerializedProperty end = p.GetEndProperty();
            if (!child.NextVisible(true))
            {
                yield break;
            }

            while (!SerializedProperty.EqualContents(child, end))
            {
                yield return child.Copy();
                if (!child.NextVisible(false))
                {
                    yield break;
                }
            }
        }

        private static string ParseReference(SerializedProperty p, string text)
        {
            if (text == "null" || text.Length == 0)
            {
                p.objectReferenceValue = null;
                return null;
            }

            Type wanted = ReferenceType(p);
            UnityEngine.Object value = FindReference(text, wanted, out string error);
            if (value == null)
            {
                return error;
            }

            p.objectReferenceValue = value;
            return p.objectReferenceValue == value ? null : "Unity refused " + text + " for a " + TypeLabel(p) + " field";
        }

        /// <summary>The object an address, component address or asset path names, as <paramref name="wanted"/>; null with why not.</summary>
        public static UnityEngine.Object FindReference(string text, Type wanted, out string error)
        {
            error = null;
            UnityEngine.Object found = null;
            if (Addresses.IsAssetPath(text) && !Addresses.SplitPrefab(text, out _, out _))
            {
                int colon = text.IndexOf(':');
                string path = colon > 0 ? text.Substring(0, colon) : text;
                string sub = colon > 0 ? text.Substring(colon + 1) : null;
                UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(path);
                if (all.Length == 0)
                {
                    error = "not found: " + path;
                    return null;
                }

                found = all.FirstOrDefault(a => a != null && wanted.IsInstanceOfType(a) && (sub == null || a.name == sub));
                if (found == null)
                {
                    error = path + (sub == null ? "" : ":" + sub) + " has no " + wanted.Name;
                }

                return found;
            }

            found = Addresses.FindObjectOrComponent(text, out error);
            if (found == null)
            {
                return null;
            }

            if (wanted.IsInstanceOfType(found))
            {
                return found;
            }

            if (found is Component owner && wanted == typeof(GameObject))
            {
                return owner.gameObject;
            }

            if (found is GameObject go && typeof(Component).IsAssignableFrom(wanted))
            {
                Component component = go.GetComponent(wanted);
                if (component != null)
                {
                    return component;
                }
            }

            error = text + " has no " + wanted.Name;
            return null;
        }

        internal static string LayerMaskText(int mask)
        {
            if (mask == 0)
            {
                return "Nothing";
            }

            if (mask == -1)
            {
                return "Everything";
            }

            var names = new List<string>();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    string name = LayerMask.LayerToName(i);
                    names.Add(string.IsNullOrEmpty(name) ? "layer " + i.ToString(CultureInfo.InvariantCulture) : name);
                }
            }

            return string.Join(", ", names);
        }

        private static string WithNumbers(string text, int count, Action<float[]> set)
        {
            if (!TryNumbers(text, count, out float[] numbers))
            {
                return NumbersHelp(count, text);
            }

            set(numbers);
            return null;
        }

        private static string Assign(Action set)
        {
            set();
            return null;
        }

        internal static FieldInfo FieldOf(SerializedProperty p)
        {
            Type type = p.serializedObject.targetObject.GetType();
            FieldInfo field = null;
            foreach (string part in p.propertyPath.Replace(".Array.data[", "[").Split('.'))
            {
                string name = part;
                int bracket = name.IndexOf('[');
                if (bracket >= 0)
                {
                    name = name.Substring(0, bracket);
                }

                field = FindField(type, name);
                if (field == null)
                {
                    return null;
                }

                type = field.FieldType;
                if (bracket >= 0)
                {
                    type = type.IsArray ? type.GetElementType() : type.IsGenericType ? type.GetGenericArguments()[0] : type;
                }
            }

            return field;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        private static float Round(float degrees)
        {
            float rounded = (float)Math.Round(degrees, 3);
            return rounded == 360f || rounded == -0f ? 0f : rounded;
        }

        internal static string Quote(string text) => "\"" + text.Replace("\n", "\\n") + "\"";

        private static string CleanType(string type)
        {
            if (type.StartsWith("PPtr<", StringComparison.Ordinal))
            {
                type = type.Substring(5).TrimEnd('>').TrimStart('$');
            }

            if (type.StartsWith("ExposedReference<", StringComparison.Ordinal))
            {
                type = type.Substring(17).TrimEnd('>');
            }

            return type;
        }
    }
}
