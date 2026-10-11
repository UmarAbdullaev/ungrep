using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Object addresses (SPEC-editor-ops, Addressing objects): `Player/Model/Hand` in the loaded
    /// scenes, `Enemy#2` for the second sibling of that name, `Main:Player` in one scene,
    /// `Assets/X.prefab` and `Assets/X.prefab:Child` in a prefab asset, `Assets/Y.asset` for an asset;
    /// `Player:Rigidbody` for a component where one is taken.
    /// </summary>
    internal static class Addresses
    {
        public const string PrefabSeparator = ".prefab:";
        public const string DontDestroyOnLoad = "DontDestroyOnLoad";

        private const int MaxCandidates = 10;
        private const int MaxNear = 5;
        private const string Ambiguous = "ambiguous: ";

        private static readonly MethodInfo _dontDestroyOnLoadGetter = typeof(EditorSceneManager).GetMethod(
            "GetDontDestroyOnLoadScene", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        // While an edit has a prefab asset's contents loaded: its root and path, so addresses
        // inside name the asset (`Assets/X.prefab:Child`) and answers say the asset is written.
        private static GameObject _contentsRoot;
        private static string _contentsPath;

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

        /// <summary>What <see cref="FindObject"/> does, as <see cref="FindObjectOrComponent(string, Finder, out string)"/> takes it.</summary>
        public delegate UnityEngine.Object Finder(string address, out string error);

        /// <summary>
        /// An object address or a component address (`Player:Rigidbody`, `Main:Player:Rigidbody`,
        /// `Assets/X.prefab:Barrel:Rigidbody`), read both ways (SPEC-editor-ops, Addressing objects):
        /// the object the whole address names, or the component after the last `:` of the object
        /// before it. When both readings match, null and an `ambiguous:` error with a form for each.
        /// </summary>
        public static UnityEngine.Object FindObjectOrComponent(string address, out string error) =>
            FindObjectOrComponent(address, FindObject, out error);

        /// <inheritdoc cref="FindObjectOrComponent(string, out string)"/>
        public static UnityEngine.Object FindObjectOrComponent(string address, Finder find, out string error)
        {
            UnityEngine.Object found = find(address, out string objectError);
            if (!SplitComponent(address, out string ownerAddress, out string name))
            {
                error = objectError;
                return found;
            }

            // An owner named by several objects is a component reading that finds several, when a
            // component type has the name.
            var owner = find(ownerAddress, out string ownerError) as GameObject;
            if (owner == null && !(IsAmbiguous(ownerError) && IsComponentType(name)))
            {
                error = objectError;
                return found;
            }

            string componentError = ownerError;
            Component component = owner == null ? null : Component(owner, name, out componentError);
            bool objectMatches = found != null || IsAmbiguous(objectError);
            bool componentMatches = component != null || IsAmbiguous(componentError);
            if (objectMatches && componentMatches)
            {
                error = "ambiguous: " + address.Trim() + " is the object " + ObjectForm(address) + " or "
                    + (owner != null ? "the component " + ComponentForm(owner, name) : "a component of " + ownerError.Substring(Ambiguous.Length));
                return null;
            }

            if (objectMatches || componentMatches)
            {
                error = objectMatches ? objectError : componentError;
                return objectMatches ? found : component;
            }

            // Neither: the object reading's miss counts too when it looked inside a scene or prefab named so.
            bool inside = ownerAddress.IndexOf(':') < 0
                && (ownerAddress.EndsWith(".prefab", StringComparison.Ordinal) || LoadedScenes().Any(s => s.name == ownerAddress));
            error = inside ? objectError + "; " + componentError : componentError;
            return null;
        }

        /// <summary>Splits `Level/Player:Rigidbody` at the last `:` into the object's address and the component.</summary>
        public static bool SplitComponent(string address, out string owner, out string component)
        {
            string text = address?.Trim() ?? "";
            int colon = text.LastIndexOf(':');
            bool split = colon > 0 && colon < text.Length - 1;
            owner = split ? text.Substring(0, colon) : null;
            component = split ? text.Substring(colon + 1).Trim() : null;
            return split;
        }

        internal static bool IsAmbiguous(string error) => error != null && error.StartsWith(Ambiguous, StringComparison.Ordinal);

        // `Rigidbody`, `AudioSource#2`: a component type's name, as Component matches it.
        private static bool IsComponentType(string name)
        {
            ParseSegment(name, out string type, out _);
            return TypeCache.GetTypesDerivedFrom<Component>().Any(t => string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase) || t.FullName == type);
        }

        // `Main:/AudioSource`: after a `/` nothing up to the last `:` can be a component type.
        private static string ObjectForm(string address)
        {
            string text = address.Trim();
            int colon = text.IndexOf(':');
            return text.Substring(0, colon + 1) + "/" + text.Substring(colon + 1).TrimStart('/');
        }

        // `Main:Main:AudioSource`, `Assets/X.prefab::AudioSource`: with the scene or the prefab's
        // empty path in front, the object reading looks for one object named `Main:AudioSource`.
        private static string ComponentForm(GameObject owner, string name)
        {
            string asset = ContentsPath(owner) ?? (EditorUtility.IsPersistent(owner) ? AssetDatabase.GetAssetPath(owner) : null);
            if (asset == null)
            {
                return owner.scene.name + ":" + string.Join("/", Segments(owner)) + ":" + name;
            }

            return owner.transform.parent == null ? asset + "::" + name : Of(owner) + ":" + name;
        }

        // Whether `Main:name` reads as a component of a root named `Main` (one; or several, when a
        // component type has the name), as FindObjectOrComponent reads it.
        private static bool RootHolds(string owner, string name)
        {
            List<GameObject> owners = Picked(LoadedScenes().SelectMany(s => s.GetRootGameObjects()), owner);
            return owners.Count == 1 ? Holds(owners[0], name) : owners.Count > 1 && IsComponentType(name);
        }

        // Whether Component finds `name` (one or several) on `target`, without building its answers:
        // they print addresses, and these checks run while printing one.
        private static bool Holds(GameObject target, string name)
        {
            ParseSegment(name.Trim(), out string type, out int index);
            int count = OfType(target.GetComponents<Component>(), type).Count;
            return count > 0 && index <= count;
        }

        // The objects a path segment names in `level` (`Enemy#2` one of them), as Walk picks them.
        private static List<GameObject> Picked(IEnumerable<GameObject> level, string segment)
        {
            ParseSegment(segment, out string name, out int index);
            List<GameObject> matches = Named(level.ToList(), name);
            return index == 0 ? matches : index <= matches.Count ? new List<GameObject> { matches[index - 1] } : new List<GameObject>();
        }

        // The objects named `name`, ignoring case when none has it exactly.
        private static List<GameObject> Named(IList<GameObject> level, string name)
        {
            List<GameObject> matches = level.Where(g => g.name == name).ToList();
            return matches.Count > 0 ? matches : level.Where(g => string.Equals(g.name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // The components of a type: by name or full name, ignoring case when none has it exactly.
        private static List<Component> OfType(Component[] all, string type)
        {
            List<Component> matches = all.Where(c => c != null && (c.GetType().Name == type || c.GetType().FullName == type)).ToList();
            return matches.Count > 0 ? matches : all.Where(c => c != null && string.Equals(c.GetType().Name, type, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>The address to show for an object: the shortest form <see cref="Find"/> resolves back to it.</summary>
        public static string Of(GameObject target)
        {
            if (target == null)
            {
                return "null";
            }

            // `Main:/Camera` and `Assets/X.prefab:/Camera` where the form without `/` would also read
            // as the Camera of an object `Main` or of the prefab's root.
            List<string> segments = Segments(target);
            string contents = ContentsPath(target);
            if (contents != null || EditorUtility.IsPersistent(target))
            {
                string asset = contents ?? AssetDatabase.GetAssetPath(target);
                string inner = string.Join("/", segments.Skip(1));
                return segments.Count == 1 ? asset
                    : asset + ":" + (segments.Count == 2 && Holds(target.transform.root.gameObject, inner) ? "/" : "") + inner;
            }

            string path = string.Join("/", segments);
            if (!NamesScene(target))
            {
                return path;
            }

            return target.scene.name + ":" + (segments.Count == 1 && RootHolds(target.scene.name, path) ? "/" : "") + path;
        }

        // The scene name leads an address with several scene files open, or with one scene file
        // when it and DontDestroyOnLoad both hold a root of this object's root name (O14).
        private static bool NamesScene(GameObject target)
        {
            if (!target.scene.IsValid())
            {
                return false;
            }

            if (SceneManager.sceneCount > 1)
            {
                return true;
            }

            Scene kept = DontDestroyOnLoadScene();
            Scene played = SceneManager.GetSceneAt(0);
            if (!kept.IsValid() || (target.scene != kept && target.scene != played))
            {
                return false;
            }

            Scene other = target.scene == kept ? played : kept;
            string root = target.transform.root.name;
            return other.isLoaded && other.GetRootGameObjects().Any(g => g.name == root);
        }

        /// <summary>
        /// The address to show for a component: `Player:Rigidbody`, or `Main:Main:Rigidbody` and
        /// `Assets/X.prefab::Rigidbody` where the short form would also name an object.
        /// </summary>
        public static string Of(Component component)
        {
            if (component == null)
            {
                return "null";
            }

            // The object reading of `address:name` looks among a prefab root's children, or among the
            // roots of a loaded scene named as the object; nowhere else without a `:` in a name.
            GameObject owner = component.gameObject;
            string name = ComponentName(component);
            string address = Of(owner);
            IEnumerable<GameObject> level = owner.transform.parent != null ? Enumerable.Empty<GameObject>()
                : address.EndsWith(".prefab", StringComparison.Ordinal) ? Children(owner)
                : LoadedScenes().Where(s => s.name == address).SelectMany(s => s.GetRootGameObjects());
            return Picked(level, name).Count > 0 ? ComponentForm(owner, name) : address + ":" + name;
        }

        /// <summary>Loads a prefab asset's contents for an edit; <see cref="UnloadContents"/> ends it.</summary>
        public static GameObject LoadContents(string prefab)
        {
            UnloadContents();
            _contentsRoot = PrefabUtility.LoadPrefabContents(prefab);
            _contentsPath = prefab;
            return _contentsRoot;
        }

        public static void UnloadContents()
        {
            if (_contentsRoot != null)
            {
                PrefabUtility.UnloadPrefabContents(_contentsRoot);
            }

            _contentsRoot = null;
            _contentsPath = null;
        }

        /// <summary>The prefab asset whose loaded contents hold <paramref name="target"/>, or null.</summary>
        public static string ContentsPath(GameObject target) =>
            _contentsRoot != null && target != null && target.transform.root.gameObject == _contentsRoot ? _contentsPath : null;

        /// <summary>The loaded scenes in hierarchy order, then in play mode DontDestroyOnLoad while it holds objects.</summary>
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

            Scene kept = DontDestroyOnLoadScene();
            if (kept.IsValid())
            {
                yield return kept;
            }
        }

        /// <summary>A loaded scene by path or name; in play mode `DontDestroyOnLoad` also while it is empty.</summary>
        public static Scene SceneNamed(string name)
        {
            Scene scene = LoadedScenes().FirstOrDefault(s => s.path == name || s.name == name);
            return scene.IsValid() || name != DontDestroyOnLoad || !EditorApplication.isPlaying ? scene : KeptScene();
        }

        /// <summary>Play mode's DontDestroyOnLoad scene while it holds objects, else an invalid scene.</summary>
        public static Scene DontDestroyOnLoadScene()
        {
            if (!EditorApplication.isPlaying)
            {
                return default;
            }

            Scene kept = KeptScene();
            return kept.IsValid() && kept.isLoaded && kept.rootCount > 0 ? kept : default;
        }

        // Unity has no public handle on DontDestroyOnLoad; EditorSceneManager's internal getter
        // (2022.3 to 6000.3) reads it without touching it, the probe stands in where it is gone.
        private static Scene KeptScene() =>
            _dontDestroyOnLoadGetter != null && _dontDestroyOnLoadGetter.ReturnType == typeof(Scene)
                ? (Scene)_dontDestroyOnLoadGetter.Invoke(null, null)
                : ProbeDontDestroyOnLoad();

        /// <summary>The scene for a Unity without the internal getter: a hidden object kept, read and destroyed at once.</summary>
        public static Scene ProbeDontDestroyOnLoad()
        {
            var probe = new GameObject("ug probe") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                UnityEngine.Object.DontDestroyOnLoad(probe);
                return probe.scene;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(probe);
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
                List<GameObject> matches = Named(level, name);
                if (matches.Count == 0)
                {
                    error = "not found: " + segment + " in " + where + Near(name, level.Select(g => g.name));
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
                        + string.Join(", ", matches.Take(MaxCandidates).Select(Of))
                        + (matches.Count > MaxCandidates ? ", ... " + (matches.Count - MaxCandidates).ToString(CultureInfo.InvariantCulture) + " more: ug find --live " + name : "");
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
            List<Component> matches = OfType(all, type);
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

        // `Level`, `Enemy#2`: the segments from the root down to the object.
        private static List<string> Segments(GameObject target)
        {
            var segments = new List<string>();
            for (Transform t = target.transform; t != null; t = t.parent)
            {
                segments.Add(Segment(t.gameObject));
            }

            segments.Reverse();
            return segments;
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

        /// <summary>` (near: A, B)` for the names close to <paramref name="name"/>, or empty.</summary>
        public static string Near(string name, IEnumerable<string> names)
        {
            string lower = name.ToLowerInvariant();
            List<string> near = names
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
