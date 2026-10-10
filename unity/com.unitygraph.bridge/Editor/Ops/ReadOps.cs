using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Live reads (SPEC-editor-ops, Endpoints): `/tree`, `/inspect`, `/find`. Nothing here changes
    /// the editor. Line caps keep answers small; the last line says what was left out.
    /// </summary>
    internal static class ReadOps
    {
        public const int MaxTreeLines = 200;
        public const int MaxInspectLines = 150;
        public const int MaxFound = 100;

        public static Reply Tree(Request request)
        {
            int depth = int.TryParse(request.Get("depth"), NumberStyles.None, CultureInfo.InvariantCulture, out int d) ? Math.Min(d, 50) : 2;
            string address = request.Get("object");
            var lines = new List<string>();
            int left = 0;
            if (string.IsNullOrWhiteSpace(address))
            {
                List<UnityEngine.SceneManagement.Scene> scenes = Addresses.LoadedScenes().ToList();
                bool several = scenes.Count > 1;
                foreach (UnityEngine.SceneManagement.Scene scene in scenes)
                {
                    if (several)
                    {
                        lines.Add(scene.name + ":");
                    }

                    foreach (GameObject root in scene.GetRootGameObjects())
                    {
                        Write(root, several ? 1 : 0, depth - 1, lines, ref left);
                    }
                }

                if (lines.Count == 0)
                {
                    lines.Add("no loaded scene has objects");
                }
            }
            else
            {
                GameObject target = Addresses.Find(address, out string error);
                if (target == null)
                {
                    return Answer.Error(error);
                }

                lines.Add(Addresses.Of(target) + Describe(target) + (depth <= 0 ? ChildCount(target) : ""));
                if (depth > 0)
                {
                    foreach (GameObject child in Addresses.Children(target))
                    {
                        Write(child, 1, depth - 1, lines, ref left);
                    }
                }
            }

            if (left > 0)
            {
                lines.Add("... " + Answer.Count(left, "more object", "more objects") + ": ug tree <object> or --depth to narrow");
            }

            return Answer.Lines(lines);
        }

        public static Reply Inspect(Request request)
        {
            UnityEngine.Object target = Addresses.FindObject(request.Get("object"), out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            string component = request.Get("component");
            if (request.Get("importer") == "true")
            {
                AssetImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(target));
                return importer == null
                    ? Answer.Error(request.Get("object") + " has no importer settings")
                    : Answer.Lines(Fields(importer, AssetDatabase.GetAssetPath(target) + "  " + importer.GetType().Name));
            }

            if (target is GameObject go)
            {
                if (string.IsNullOrWhiteSpace(component))
                {
                    return Answer.Lines(Overview(go));
                }

                Component found = Addresses.Component(go, component, out error);
                return found == null
                    ? Answer.Error(error)
                    : Answer.Lines(Fields(found, Addresses.Of(go) + "  " + Addresses.ComponentName(found) + Script(found)));
            }

            if (target is Material material)
            {
                return Answer.Lines(MaterialLines(material));
            }

            return Answer.Lines(Fields(target, AssetDatabase.GetAssetPath(target) + "  " + target.GetType().Name + Script(target)));
        }

        /// <summary>Live objects matching every given filter: name (part, any case), tag, layer, component type.</summary>
        public static Reply Find(Request request)
        {
            string name = request.Get("name");
            string tag = request.Get("tag");
            string layerText = request.Get("layer");
            string component = request.Get("component");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(tag) && string.IsNullOrEmpty(layerText) && string.IsNullOrEmpty(component))
            {
                return Answer.Error("give a name, --tag, --layer or --component");
            }

            int layer = -1;
            if (!string.IsNullOrEmpty(layerText))
            {
                layer = int.TryParse(layerText, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : LayerMask.NameToLayer(layerText);
                if (layer < 0 || layer > 31)
                {
                    return Answer.Error("no layer " + layerText);
                }
            }

            var lines = new List<string>();
            int total = 0;
            foreach (UnityEngine.SceneManagement.Scene scene in Addresses.LoadedScenes())
            {
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        GameObject go = t.gameObject;
                        if ((string.IsNullOrEmpty(name) || go.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                            && (string.IsNullOrEmpty(tag) || go.tag == tag)
                            && (layer < 0 || go.layer == layer)
                            && (string.IsNullOrEmpty(component) || HasComponent(go, component)))
                        {
                            if (++total <= MaxFound)
                            {
                                lines.Add(Addresses.Of(go) + (go.activeInHierarchy ? "" : "  inactive"));
                            }
                        }
                    }
                }
            }

            if (total == 0)
            {
                lines.Add("no object in the loaded scenes matches");
            }
            else if (total > MaxFound)
            {
                lines.Add("... " + Answer.Count(total - MaxFound, "more", "more") + " (" + total.ToString(CultureInfo.InvariantCulture) + " in all): narrow the filters");
            }

            return Answer.Lines(lines);
        }

        /// <summary>`  Camera, AudioListener  inactive  [Turret.prefab]`: what the tree shows after a name.</summary>
        public static string Describe(GameObject go)
        {
            string text = "";
            List<string> components = go.GetComponents<Component>().Where(c => !(c is Transform) || c is RectTransform).Select(Addresses.ComponentName).ToList();
            if (components.Count > 0)
            {
                text += "  " + string.Join(", ", components);
            }

            if (!go.activeSelf)
            {
                text += "  inactive";
            }

            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                string path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                text += "  [" + (string.IsNullOrEmpty(path) ? "missing prefab" : System.IO.Path.GetFileName(path)) + "]";
            }

            return text;
        }

        private static void Write(GameObject go, int indent, int levels, List<string> lines, ref int left)
        {
            if (lines.Count >= MaxTreeLines)
            {
                left += go.GetComponentsInChildren<Transform>(true).Length;
                return;
            }

            lines.Add(new string(' ', indent * 2) + Addresses.Segment(go) + Describe(go) + (levels <= 0 ? ChildCount(go) : ""));
            if (levels > 0)
            {
                foreach (GameObject child in Addresses.Children(go))
                {
                    Write(child, indent + 1, levels - 1, lines, ref left);
                }
            }
        }

        private static string ChildCount(GameObject go) =>
            go.transform.childCount > 0 ? "  (" + Answer.Count(go.transform.childCount, "child", "children") + ")" : "";

        private static List<string> Overview(GameObject go)
        {
            string head = Addresses.Of(go);
            if (!go.activeSelf)
            {
                head += "  inactive";
            }
            else if (!go.activeInHierarchy && go.scene.IsValid())
            {
                head += "  inactive parent";
            }

            if (go.tag != "Untagged")
            {
                head += "  tag " + go.tag;
            }

            if (go.layer != 0)
            {
                head += "  layer " + LayerMask.LayerToName(go.layer);
            }

            if (PrefabUtility.IsPartOfPrefabInstance(go))
            {
                GameObject root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
                head += "  in prefab instance " + (root == go ? "" : Addresses.Of(root) + " ") + "of " + PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                if (root == go)
                {
                    int overrides = PrefabUtility.GetObjectOverrides(go).Count + PrefabUtility.GetAddedComponents(go).Count
                        + PrefabUtility.GetRemovedComponents(go).Count + PrefabUtility.GetAddedGameObjects(go).Count;
                    head += " (" + Answer.Count(overrides, "override", "overrides") + ")";
                }
            }

            var lines = new List<string> { head };
            foreach (Component component in go.GetComponents<Component>())
            {
                string line = "  " + Addresses.ComponentName(component);
                if (component is Transform transform && !(component is RectTransform))
                {
                    line += "  position " + Vector(transform.localPosition) + "  rotation " + Vector(transform.localEulerAngles) + "  scale " + Vector(transform.localScale);
                }
                else if (component != null)
                {
                    line += Script(component);
                    if (component is Behaviour behaviour && !behaviour.enabled)
                    {
                        line += "  disabled";
                    }
                }

                lines.Add(line);
            }

            List<GameObject> children = Addresses.Children(go);
            if (children.Count > 0)
            {
                lines.Add("  children: " + string.Join(", ", children.Take(Values.MaxItems).Select(Addresses.Segment))
                    + (children.Count > Values.MaxItems ? ", ... " + (children.Count - Values.MaxItems).ToString(CultureInfo.InvariantCulture) + " more" : ""));
            }

            return lines;
        }

        internal static List<string> Fields(UnityEngine.Object target, string head)
        {
            var lines = new List<string> { head };
            using (var serialized = new SerializedObject(target))
            {
                int total = 0;
                foreach (SerializedProperty p in Values.Fields(serialized))
                {
                    if (++total > MaxInspectLines)
                    {
                        continue;
                    }

                    lines.Add("  " + Values.ShownName(p.name) + "  " + Values.TypeLabel(p) + "  " + Values.Format(p));
                }

                if (total > MaxInspectLines)
                {
                    lines.Add("  ... " + Answer.Count(total - MaxInspectLines, "more field", "more fields"));
                }
                else if (total == 0)
                {
                    lines.Add("  no visible serialized fields");
                }
            }

            return lines;
        }

        private static List<string> MaterialLines(Material material)
        {
            Shader shader = material.shader;
            var lines = new List<string> { AssetDatabase.GetAssetPath(material) + "  Material  shader " + (shader == null ? "missing" : shader.name) };
            if (shader == null)
            {
                return lines;
            }

            for (int i = 0; i < shader.GetPropertyCount() && lines.Count <= MaxInspectLines; i++)
            {
                if ((shader.GetPropertyFlags(i) & ShaderPropertyFlags.HideInInspector) != 0)
                {
                    continue;
                }

                string name = shader.GetPropertyName(i);
                lines.Add("  " + name + "  " + MaterialProperty(material, shader.GetPropertyType(i), name));
            }

            if (material.shaderKeywords.Length > 0)
            {
                lines.Add("  keywords  " + string.Join(", ", material.shaderKeywords));
            }

            return lines;
        }

        public static string MaterialProperty(Material material, ShaderPropertyType type, string name)
        {
            switch (type)
            {
                case ShaderPropertyType.Color:
                    Color c = material.GetColor(name);
                    return "Color  " + Values.Numbers(c.r, c.g, c.b, c.a);
                case ShaderPropertyType.Vector:
                    Vector4 v = material.GetVector(name);
                    return "Vector  " + Values.Numbers(v.x, v.y, v.z, v.w);
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    return "float  " + Values.Number(material.GetFloat(name));
                case ShaderPropertyType.Int:
                    return "int  " + material.GetInteger(name).ToString(CultureInfo.InvariantCulture);
                case ShaderPropertyType.Texture:
                    Texture texture = material.GetTexture(name);
                    Vector2 scale = material.GetTextureScale(name);
                    Vector2 offset = material.GetTextureOffset(name);
                    string text = "Texture  " + Values.Reference(texture, 0);
                    if (scale != Vector2.one || offset != Vector2.zero)
                    {
                        text += "  tiling " + Values.Numbers(scale.x, scale.y) + "  offset " + Values.Numbers(offset.x, offset.y);
                    }

                    return text;
                default:
                    return type.ToString();
            }
        }

        private static bool HasComponent(GameObject go, string type) =>
            go.GetComponents<Component>().Any(c =>
            {
                for (Type t = c == null ? null : c.GetType(); t != null && t != typeof(Component); t = t.BaseType)
                {
                    if (string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase) || t.FullName == type)
                    {
                        return true;
                    }
                }

                return false;
            });

        private static string Script(UnityEngine.Object target)
        {
            MonoScript script = target is MonoBehaviour behaviour ? MonoScript.FromMonoBehaviour(behaviour)
                : target is ScriptableObject scriptable ? MonoScript.FromScriptableObject(scriptable)
                : null;
            string path = script == null ? null : AssetDatabase.GetAssetPath(script);
            return string.IsNullOrEmpty(path) ? "" : "  " + path;
        }

        private static string Vector(Vector3 v) => Values.Numbers(Round(v.x), Round(v.y), Round(v.z));

        private static float Round(float value) => (float)Math.Round(value, 4);
    }
}
