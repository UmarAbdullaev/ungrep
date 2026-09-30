using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Object addresses (SPEC-editor-ops, Addressing objects): `Player/Model/Hand` in the loaded
    /// scenes, `Enemy#2` for the second sibling of that name, `Main:Player` in one scene,
    /// `Assets/X.prefab` and `Assets/X.prefab:Child` in a prefab asset, `Assets/Y.asset` for an asset.
    /// </summary>
    internal static class Addresses
    {
        public const string PrefabSeparator = ".prefab:";

        private const int MaxCandidates = 10;
        private const int MaxNear = 5;

        public static bool IsAssetPath(string address) =>
            address.StartsWith("Assets/", StringComparison.Ordinal) || address.StartsWith("Packages/", StringComparison.Ordinal)
            || address.StartsWith("ProjectSettings/", StringComparison.Ordinal);

        /// <summary>A game object in the loaded scenes or a prefab asset, or null with <paramref name="error"/>.</summary>
        public static GameObject Find(string address, out string error)
        {
            UnityEngine.Object found = FindObject(address, out error);
            if (found == null || found is GameObject)
            {
                return (GameObject)found;
            }

            error = address.Trim() + " is a " + found.GetType().Name + ", not a game object";
            return null;
        }

        /// <summary>A game object, or an asset's main object for asset paths that are not prefabs.</summary>
        public static UnityEngine.Object FindObject(string address, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "no object given";
                return null;
            }

            address = address.Trim();
            if (IsAssetPath(address))
            {
                return FindInAsset(address, out error);
            }

            string sceneName = null;
            string path = address;
            int colon = address.IndexOf(':');
            if (colon > 0)
            {
                string prefix = address.Substring(0, colon);
                if (LoadedScenes().Any(s => s.name == prefix))
                {
                    sceneName = prefix;
                    path = address.Substring(colon + 1);
                }
            }

            List<GameObject> roots = LoadedScenes()
                .Where(s => sceneName == null || s.name == sceneName)
                .SelectMany(s => s.GetRootGameObjects())
                .ToList();
            string where = sceneName == null ? "the loaded scenes" : sceneName;
            return Walk(roots, path, where, out error);
        }

        /// <summary>The address to show for an object: the shortest form <see cref="Find"/> resolves back to it.</summary>
        public static string Of(GameObject target)
        {
            if (target == null)
            {
                return "null";
            }

            var segments = new List<string>();
            for (Transform t = target.transform; t != null; t = t.parent)
            {
                segments.Add(Segment(t.gameObject));
            }

            segments.Reverse();
            if (EditorUtility.IsPersistent(target))
            {
                string asset = AssetDatabase.GetAssetPath(target);
                return segments.Count == 1 ? asset : asset + ":" + string.Join("/", segments.Skip(1));
            }

            string path = string.Join("/", segments);
            return SceneManager.sceneCount > 1 && target.scene.IsValid() ? target.scene.name + ":" + path : path;
        }

        /// <summary>The loaded scenes in hierarchy order.</summary>
        public static IEnumerable<Scene> LoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                {
                    yield return scene;
                }
            }
        }

        /// <summary>Resolves a path of names below <paramref name="level"/>; also used inside loaded prefab contents.</summary>
        public static GameObject Walk(IList<GameObject> level, string path, string where, out string error)
        {
            error = null;
            string[] segments = path.Trim('/').Split('/');
            if (segments.Length == 0 || segments.Any(s => s.Length == 0))
            {
                error = "no object path in " + where;
                return null;
            }

            GameObject current = null;
            foreach (string segment in segments)
            {
                ParseSegment(segment, out string name, out int index);
                List<GameObject> matches = level.Where(g => g.name == name).ToList();
                if (matches.Count == 0)
                {
                    matches = level.Where(g => string.Equals(g.name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (matches.Count == 0)
                {
                    error = "not found: " + segment + " in " + where + Near(name, level);
                    return null;
                }

                if (index > 0)
                {
                    if (index > matches.Count)
                    {
                        error = "not found: " + segment + " in " + where + " (" + matches.Count.ToString(CultureInfo.InvariantCulture) + " named " + name + ")";
                        return null;
                    }

                    current = matches[index - 1];
                }
                else if (matches.Count > 1)
                {
                    error = "ambiguous: " + name + " (" + matches.Count.ToString(CultureInfo.InvariantCulture) + " in " + where + "): "
                        + string.Join(", ", matches.Take(MaxCandidates).Select(Of));
                    return null;
                }
                else
                {
                    current = matches[0];
                }

                where = Of(current);
                level = Children(current);
            }

            return current;
        }

        public static List<GameObject> Children(GameObject parent)
        {
            var children = new List<GameObject>(parent.transform.childCount);
            foreach (Transform child in parent.transform)
            {
                children.Add(child.gameObject);
            }

            return children;
        }

        /// <summary>Splits `Assets/X.prefab:Inner/Path` into the prefab path and the inner path (empty for the root).</summary>
        public static bool SplitPrefab(string address, out string prefab, out string inner)
        {
            int at = address.IndexOf(PrefabSeparator, StringComparison.Ordinal);
            if (at >= 0)
            {
                prefab = address.Substring(0, at + ".prefab".Length);
                inner = address.Substring(at + PrefabSeparator.Length).Trim('/');
                return true;
            }

            prefab = address;
            inner = "";
            return address.EndsWith(".prefab", StringComparison.Ordinal);
        }

        private static UnityEngine.Object FindInAsset(string address, out string error)
        {
            error = null;
            if (SplitPrefab(address, out string prefab, out string inner))
            {
                GameObject root = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
                if (root == null)
                {
                    error = "not found: " + prefab;
                    return null;
                }

                return inner.Length == 0 ? root : Walk(Children(root), inner, prefab, out error);
            }

            UnityEngine.Object asset = address.StartsWith("ProjectSettings/", StringComparison.Ordinal)
                ? AssetDatabase.LoadAllAssetsAtPath(address).FirstOrDefault()
                : AssetDatabase.LoadMainAssetAtPath(address);
            if (asset == null)
            {
                error = "not found: " + address;
            }

            return asset;
        }

        /// <summary>
        /// A component of <paramref name="target"/> by type name (`Rigidbody`, full name when two
        /// types share a name), `#n` for the n-th of that type.
        /// </summary>
        public static Component Component(GameObject target, string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "no component given";
                return null;
            }

            ParseSegment(name.Trim(), out string type, out int index);
            Component[] all = target.GetComponents<Component>();
            List<Component> matches = all.Where(c => c != null && (c.GetType().Name == type || c.GetType().FullName == type)).ToList();
            if (matches.Count == 0)
            {
                matches = all.Where(c => c != null && string.Equals(c.GetType().Name, type, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (matches.Count == 0)
            {
                error = Of(target) + " has no " + type + " (has: " + string.Join(", ", all.Select(ComponentName)) + ")";
                return null;
            }

            if (index > matches.Count)
            {
                error = Of(target) + " has " + matches.Count.ToString(CultureInfo.InvariantCulture) + " " + type + ", not " + index.ToString(CultureInfo.InvariantCulture);
                return null;
            }

            if (index == 0 && matches.Count > 1)
            {
                error = "ambiguous: " + Of(target) + " has " + matches.Count.ToString(CultureInfo.InvariantCulture) + " " + type + ": "
                    + string.Join(", ", Enumerable.Range(1, matches.Count).Select(i => type + "#" + i.ToString(CultureInfo.InvariantCulture)));
                return null;
            }

            return matches[Math.Max(index, 1) - 1];
        }

        /// <summary>`Rigidbody`, `AudioSource#2` when an object holds several of a type, `missing script`.</summary>
        public static string ComponentName(Component component)
        {
            if (component == null)
            {
                return "missing script";
            }

            Type type = component.GetType();
            Component[] same = component.GetComponents(type).Where(c => c.GetType() == type).ToArray();
            return same.Length > 1 ? type.Name + "#" + (Array.IndexOf(same, component) + 1).ToString(CultureInfo.InvariantCulture) : type.Name;
        }

        private static void ParseSegment(string segment, out string name, out int index)
        {
            name = segment;
            index = 0;
            int hash = segment.LastIndexOf('#');
            if (hash > 0 && int.TryParse(segment.Substring(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0)
            {
                name = segment.Substring(0, hash);
                index = n;
            }
        }

        // `Enemy#2` when the parent (or scene root) holds more than one `Enemy`.
        public static string Segment(GameObject target)
        {
            IList<GameObject> siblings = target.transform.parent != null
                ? (IList<GameObject>)Children(target.transform.parent.gameObject)
                : target.scene.IsValid() && target.scene.isLoaded ? target.scene.GetRootGameObjects() : new[] { target };
            List<GameObject> same = siblings.Where(g => g.name == target.name).ToList();
            return same.Count > 1 ? target.name + "#" + (same.IndexOf(target) + 1).ToString(CultureInfo.InvariantCulture) : target.name;
        }

        private static string Near(string name, IEnumerable<GameObject> level)
        {
            string lower = name.ToLowerInvariant();
            List<string> near = level
                .Select(g => g.name)
                .Distinct(StringComparer.Ordinal)
                .Select(n => (Name: n, Distance: Distance(lower, n.ToLowerInvariant())))
                .Where(c => c.Distance <= Math.Max(2, name.Length / 3) || c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Distance)
                .ThenBy(c => c.Name, StringComparer.Ordinal)
                .Take(MaxNear)
                .Select(c => c.Name)
                .ToList();
            return near.Count == 0 ? "" : " (near: " + string.Join(", ", near) + ")";
        }

        private static int Distance(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
            {
                previous[j] = j;
            }

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }
    }
}
