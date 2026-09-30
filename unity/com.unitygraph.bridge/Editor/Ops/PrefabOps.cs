using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Prefab requests on scene objects (SPEC-editor-ops, Endpoints): save an object as a prefab and
    /// keep it linked, apply or revert an instance's overrides, unpack an instance. Each is one undo
    /// group named `ug prefab <sub>`; the scene stays unsaved until `ug save`. Create and apply write
    /// a prefab asset at once (Safety rule 3), so they refuse packages and vendored folders (rule 7).
    /// </summary>
    internal static class PrefabOps
    {
        private const int MaxLines = 40;

        /// <summary>`/prefab/create?object=&amp;path=`: a scene object saved as a new prefab asset, kept linked.</summary>
        public static Reply Create(Request request)
        {
            string address = request.Get("object");
            string path = request.Get("path");
            if (string.IsNullOrWhiteSpace(address))
            {
                return Answer.Error("no object given");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return Answer.Error("no prefab path given");
            }

            address = address.Trim();
            path = path.Trim();
            string refused = EditOps.ReadOnly(address) ?? Vendored(request, path) ?? PathProblem(path);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            GameObject target = Addresses.Find(address, out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            if (EditorUtility.IsPersistent(target) || Addresses.ContentsPath(target) != null)
            {
                return Answer.Error(address + " is in a prefab asset, not a scene: ug asset duplicate copies an asset");
            }

            refused = PartOfInstance(target);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            string from = Addresses.Of(target);
            string variantOf = PrefabUtility.IsOutermostPrefabInstanceRoot(target) ? PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target) : null;
            return Answer.Undoable("prefab create", () =>
            {
                PrefabUtility.SaveAsPrefabAssetAndConnect(target, path, InteractionMode.UserAction, out bool saved);
                Named(target);
                return saved
                    ? Answer.Line("created " + path + " from " + from + (variantOf == null ? "" : " (variant of " + variantOf + ")") + Written(path, target))
                    : Answer.Error("Unity did not save " + path + " from " + from + ": ug console says why");
            });
        }

        /// <summary>`/prefab/apply?object=`: an instance's overrides written into its prefab asset.</summary>
        public static Reply Apply(Request request) => Instance(request, "prefab apply", (target, asset) =>
        {
            string refused = Vendored(request, asset);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            string address = Addresses.Of(target);
            if (!PrefabUtility.HasPrefabInstanceAnyOverrides(target, false))
            {
                return Answer.Line("no overrides on " + address + " to apply");
            }

            List<string> lines = Overrides(target, out int count);
            PrefabUtility.ApplyPrefabInstance(target, InteractionMode.UserAction);
            lines.Insert(0, "applied " + Answer.Count(count, "override", "overrides") + " from " + address + " to " + asset + Written(asset, target));
            return Answer.Lines(lines);
        });

        /// <summary>`/prefab/revert?object=`: an instance's overrides thrown away, back to its prefab asset's values.</summary>
        public static Reply Revert(Request request) => Instance(request, "prefab revert", (target, asset) =>
        {
            string address = Addresses.Of(target);
            if (!PrefabUtility.HasPrefabInstanceAnyOverrides(target, false))
            {
                return Answer.Line("no overrides on " + address + " to revert");
            }

            List<string> lines = Overrides(target, out int count);
            PrefabUtility.RevertPrefabInstance(target, InteractionMode.UserAction);
            lines.Insert(0, "reverted " + Answer.Count(count, "override", "overrides") + " on " + address + Answer.Unsaved(target));
            return Answer.Lines(lines);
        });

        /// <summary>`/prefab/unpack?object=&amp;completely=`: an instance cut loose from its prefab (its outermost root, or all nested levels).</summary>
        public static Reply Unpack(Request request)
        {
            bool completely = request.Get("completely") == "true";
            return Instance(request, "prefab unpack", (target, asset) =>
            {
                string address = Addresses.Of(target);
                PrefabUtility.UnpackPrefabInstance(target, completely ? PrefabUnpackMode.Completely : PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction);
                return Answer.Line("unpacked " + address + " from " + asset + (completely ? " completely" : "") + Answer.Unsaved(target));
            });
        }

        // Resolves the instance root the request names and runs the edit as one undo group.
        private static Reply Instance(Request request, string command, Func<GameObject, string, Reply> edit)
        {
            string address = request.Get("object");
            if (string.IsNullOrWhiteSpace(address))
            {
                return Answer.Error("no object given");
            }

            address = address.Trim();
            string refused = EditOps.ReadOnly(address);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            GameObject target = Addresses.Find(address, out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            if (EditorUtility.IsPersistent(target) || Addresses.ContentsPath(target) != null)
            {
                return Answer.Error(address + " is in a prefab asset: " + command + " works on prefab instances in the loaded scenes");
            }

            refused = PartOfInstance(target);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(target))
            {
                return Answer.Error(address + " is not a prefab instance: ug prefab create " + address + " <path> makes a prefab from it");
            }

            string asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(target);
            if (string.IsNullOrEmpty(asset))
            {
                return Answer.Error(address + " is an instance of a missing prefab");
            }

            return Answer.Undoable(command, () =>
            {
                Reply reply = edit(target, asset);
                Named(target);
                return reply;
            });
        }

        // Unity's prefab operations register undo records under their own names, and the last record
        // names the merged group: end with one of ours so Edit > Undo says `ug prefab <sub>`.
        private static void Named(GameObject target) => Undo.RegisterCompleteObjectUndo(target, Answer.GroupName);

        // Objects inside an instance are not roots: name the root to use.
        private static string PartOfInstance(GameObject target)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(target) || PrefabUtility.IsOutermostPrefabInstanceRoot(target))
            {
                return null;
            }

            string root = Addresses.Of(PrefabUtility.GetOutermostPrefabInstanceRoot(target));
            return Addresses.Of(target) + " is part of the prefab instance " + root + ": give " + root;
        }

        private static string PathProblem(string path)
        {
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || path.IndexOf('\\') >= 0 || path.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
            {
                return "the prefab path must be a project path under Assets/, like Assets/Prefabs/Crate.prefab: " + path;
            }

            if (!path.EndsWith(".prefab", StringComparison.Ordinal))
            {
                return "the prefab path must end in .prefab: " + path;
            }

            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets)) || File.Exists(path))
            {
                return path + " already exists: ug does not overwrite it";
            }

            string folder = path.Substring(0, path.LastIndexOf('/'));
            return AssetDatabase.IsValidFolder(folder) ? null : "the folder " + folder + " does not exist";
        }

        // The client's vendored folders (Safety rule 7) for a prefab asset this request would write.
        private static string Vendored(Request request, string asset)
        {
            if (asset.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return asset + " is in a package, which ug does not edit: copy it into Assets/ first";
            }

            string prefixes = request.Get("vendored");
            string prefix = string.IsNullOrEmpty(prefixes)
                ? null
                : prefixes.Split('|').FirstOrDefault(p => p.Length > 0 && asset.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            return prefix == null ? null : asset + " is in the vendored folder " + prefix + ", which ug does not edit";
        }

        // `  (written Assets/X.prefab; Main.unity modified, not saved)`.
        private static string Written(string asset, GameObject scene) =>
            "  (written " + asset + "; " + Answer.Unsaved(scene).Substring("  (".Length);

        /// <summary>
        /// The overrides on an instance, one line per object that has some: changed fields as
        /// `Component.field`, and added or removed components and objects. Root name, position and
        /// rotation, which every scene instance overrides, are left out.
        /// </summary>
        private static List<string> Overrides(GameObject root, out int count)
        {
            var order = new List<GameObject>();
            var items = new Dictionary<GameObject, List<string>>();
            int total = 0;

            void Add(GameObject on, string item)
            {
                if (!items.TryGetValue(on, out List<string> list))
                {
                    items[on] = list = new List<string>();
                    order.Add(on);
                }

                if (!list.Contains(item))
                {
                    list.Add(item);
                    total++;
                }
            }

            var owners = new Dictionary<UnityEngine.Object, UnityEngine.Object>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                Map(owners, t.gameObject);
                foreach (Component component in t.GetComponents<Component>())
                {
                    Map(owners, component);
                }
            }

            PropertyModification[] modifications = PrefabUtility.GetPropertyModifications(root) ?? new PropertyModification[0];
            foreach (PropertyModification modification in modifications)
            {
                if (modification.target == null || PrefabUtility.IsDefaultOverride(modification))
                {
                    continue;
                }

                if (!owners.TryGetValue(modification.target, out UnityEngine.Object owner))
                {
                    Add(root, modification.target.GetType().Name + "." + modification.propertyPath);
                }
                else if (owner is Component component)
                {
                    Add(component.gameObject, Addresses.ComponentName(component) + "." + modification.propertyPath);
                }
                else
                {
                    Add((GameObject)owner, modification.propertyPath);
                }
            }

            foreach (var added in PrefabUtility.GetAddedComponents(root))
            {
                Add(added.instanceComponent.gameObject, "added " + Addresses.ComponentName(added.instanceComponent));
            }

            foreach (var removed in PrefabUtility.GetRemovedComponents(root))
            {
                Add(removed.containingInstanceGameObject, "removed " + removed.assetComponent.GetType().Name);
            }

            foreach (var added in PrefabUtility.GetAddedGameObjects(root))
            {
                Add(added.instanceGameObject, "added object");
            }

            foreach (var removed in PrefabUtility.GetRemovedGameObjects(root))
            {
                Add(removed.parentOfRemovedGameObjectInInstance, "removed object " + removed.assetGameObject.name);
            }

            count = total;
            List<string> lines = order.Take(MaxLines)
                .Select(go => "  " + Addresses.Of(go) + "  " + string.Join(", ", items[go]))
                .ToList();
            if (order.Count > MaxLines)
            {
                lines.Add("... " + Answer.Count(order.Count - MaxLines, "more object", "more objects"));
            }

            return lines;
        }

        private static void Map(Dictionary<UnityEngine.Object, UnityEngine.Object> owners, UnityEngine.Object instance)
        {
            UnityEngine.Object source = instance == null ? null : PrefabUtility.GetCorrespondingObjectFromSource(instance);
            if (source != null)
            {
                owners[source] = instance;
            }
        }
    }
}
