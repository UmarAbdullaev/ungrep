using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Edits of objects, components and fields (SPEC-editor-ops, Endpoints and Safety rules). In the
    /// loaded scenes every request is one undo group and stays unsaved until `ug save`; inside a
    /// prefab asset (`Assets/X.prefab:Child`) the edit loads the prefab's contents, applies there
    /// and writes the asset at once.
    /// </summary>
    internal static class EditOps
    {
        private const int MaxSelected = 20;

        private static readonly string[] _primitives = Enum.GetNames(typeof(PrimitiveType));

        public static Reply Create(Request request)
        {
            string name = request.Get("name");
            string prefabPath = request.Get("prefab");
            string primitive = request.Get("primitive");
            string parent = request.Get("parent");
            if (name != null && name.IndexOf('/') >= 0)
            {
                return Answer.Error(SlashName);
            }

            GameObject asset = null;
            PrimitiveType shape = PrimitiveType.Cube;
            if (!string.IsNullOrWhiteSpace(prefabPath))
            {
                prefabPath = prefabPath.Trim();
                asset = prefabPath.EndsWith(".prefab", StringComparison.Ordinal) ? AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) : null;
                if (asset == null)
                {
                    return Answer.Error("not a prefab: " + prefabPath);
                }
            }
            else if (!string.IsNullOrWhiteSpace(primitive))
            {
                string known = _primitives.FirstOrDefault(p => string.Equals(p, primitive.Trim(), StringComparison.OrdinalIgnoreCase));
                if (known == null)
                {
                    return Answer.Error("no primitive " + primitive + " (" + string.Join(", ", _primitives) + ")");
                }

                shape = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), known);
            }

            Func<Transform, Scene, Reply> create = (under, scene) =>
            {
                if (asset != null && under != null && Addresses.ContentsPath(under.gameObject) == prefabPath)
                {
                    return Answer.Error("a prefab cannot contain itself: " + prefabPath);
                }

                GameObject made;
                string kind = "";
                if (asset != null)
                {
                    made = (GameObject)(under != null ? PrefabUtility.InstantiatePrefab(asset, under) : PrefabUtility.InstantiatePrefab(asset, scene));
                    kind = " (" + asset.name + " prefab instance)";
                }
                else
                {
                    made = string.IsNullOrWhiteSpace(primitive) ? new GameObject("GameObject") : GameObject.CreatePrimitive(shape);
                    kind = string.IsNullOrWhiteSpace(primitive) ? "" : " (" + shape + " primitive)";
                    if (under != null)
                    {
                        made.transform.SetParent(under, false);
                    }
                    else if (made.scene != scene)
                    {
                        SceneManager.MoveGameObjectToScene(made, scene);
                    }
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    made.name = name.Trim();
                }

                if (Addresses.ContentsPath(made) == null)
                {
                    Undo.RegisterCreatedObjectUndo(made, Answer.GroupName);
                }

                return Answer.Line("created " + Addresses.Of(made) + kind + Answer.Unsaved(made));
            };

            if (!string.IsNullOrWhiteSpace(parent))
            {
                return Edit("object create", parent, target => create(target.transform, target.scene));
            }

            Scene into = SceneManager.GetActiveScene();
            string sceneName = request.Get("scene");
            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                into = FindScene(sceneName);
                if (!into.IsValid())
                {
                    return Answer.Error(sceneName + " is not open");
                }
            }

            return Answer.Undoable("object create", () => create(null, into));
        }

        public static Reply Delete(Request request) => Edit("object delete", request.Get("object"), target =>
        {
            string refused = Root(target, "deleted", "ug asset delete") ?? InstancePart(target);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            string address = Addresses.Of(target);
            string unsaved = Answer.Unsaved(target);
            int below = target.GetComponentsInChildren<Transform>(true).Length - 1;
            Destroy(target);
            return Answer.Line("deleted " + address + (below > 0 ? " and " + Answer.Count(below, "object", "objects") + " under it" : "") + unsaved);
        });

        public static Reply Rename(Request request)
        {
            string name = request.Get("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                return Answer.Error("no new name given");
            }

            if (name.IndexOf('/') >= 0)
            {
                return Answer.Error(SlashName);
            }

            return Edit("object rename", request.Get("object"), target =>
            {
                string before = Addresses.Of(target);
                Record(target);
                target.name = name.Trim();
                return Answer.Line("renamed " + before + " -> " + Addresses.Of(target) + Answer.Unsaved(target));
            });
        }

        public static Reply Active(Request request)
        {
            string value = (request.Get("value") ?? "").Trim();
            if (value != "true" && value != "false")
            {
                return Answer.Error("not true or false: " + value);
            }

            return Edit("object active", request.Get("object"), target =>
            {
                bool before = target.activeSelf;
                Record(target);
                target.SetActive(value == "true");
                return Answer.Line(Addresses.Of(target) + "  active " + Bool(before) + " -> " + Bool(target.activeSelf) + Answer.Unsaved(target));
            });
        }

        public static Reply Move(Request request) => Edit("object move", request.Get("object"), target =>
        {
            string refused = Root(target, "moved", null) ?? InstancePart(target);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            string parentAddress = request.Get("parent");
            string sceneName = request.Get("scene");
            string indexText = request.Get("index");
            int index = -1;
            if (!string.IsNullOrWhiteSpace(indexText) && (!int.TryParse(indexText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out index)))
            {
                return Answer.Error("not an index (0 is first): " + indexText);
            }

            string before = Addresses.Of(target);
            Transform parent = target.transform.parent;
            if (parentAddress != null)
            {
                parent = null;
                if (parentAddress.Trim().Length > 0)
                {
                    GameObject found = FindBeside(target, parentAddress, out string error);
                    if (found == null)
                    {
                        return Answer.Error(error);
                    }

                    if (found == target || found.transform.IsChildOf(target.transform))
                    {
                        return Answer.Error("cannot move " + before + " under itself or its children");
                    }

                    parent = found.transform;
                }
            }

            Scene scene = default;
            if (!string.IsNullOrWhiteSpace(sceneName))
            {
                if (parent != null)
                {
                    return Answer.Error("--scene moves an object to a scene's root: give no --parent");
                }

                scene = FindScene(sceneName);
                if (!scene.IsValid() || Addresses.ContentsPath(target) != null)
                {
                    return Answer.Error(sceneName + " is not open");
                }
            }

            bool undo = Addresses.ContentsPath(target) == null;
            if (parent != target.transform.parent)
            {
                if (undo)
                {
                    Undo.SetTransformParent(target.transform, parent, true, Answer.GroupName);
                }
                else
                {
                    target.transform.SetParent(parent, true);
                }
            }

            if (scene.IsValid() && target.scene != scene)
            {
                Undo.MoveGameObjectToScene(target, scene, Answer.GroupName);
            }

            if (index >= 0)
            {
                if (undo)
                {
                    Undo.RegisterFullObjectHierarchyUndo(parent != null ? parent.gameObject : target, Answer.GroupName);
                }

                target.transform.SetSiblingIndex(index);
            }

            string at = index >= 0 ? " at index " + target.transform.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) : "";
            return Answer.Line("moved " + before + " -> " + Addresses.Of(target) + at + Answer.Unsaved(target));
        });

        public static Reply Duplicate(Request request)
        {
            string name = request.Get("name");
            if (name != null && name.IndexOf('/') >= 0)
            {
                return Answer.Error(SlashName);
            }

            return Edit("object duplicate", request.Get("object"), target =>
            {
                string refused = Root(target, "duplicated", "ug asset duplicate");
                if (refused != null)
                {
                    return Answer.Error(refused);
                }

                GameObject copy = Addresses.ContentsPath(target) == null ? DuplicateInScene(target) : null;
                if (copy == null)
                {
                    copy = UnityEngine.Object.Instantiate(target, target.transform.parent);
                    copy.name = target.name;
                    copy.transform.SetSiblingIndex(target.transform.GetSiblingIndex() + 1);
                    if (Addresses.ContentsPath(copy) == null)
                    {
                        Undo.RegisterCreatedObjectUndo(copy, Answer.GroupName);
                    }
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    copy.name = name.Trim();
                }

                return Answer.Line("duplicated " + Addresses.Of(target) + " -> " + Addresses.Of(copy) + Answer.Unsaved(copy));
            });
        }

        public static Reply AddComponent(Request request)
        {
            Type type = ComponentType(request.Get("type"), out string error);
            if (type == null)
            {
                return Answer.Error(error);
            }

            return Edit("component add", request.Get("object"), target =>
            {
                if (type == typeof(Transform))
                {
                    return Answer.Error(Addresses.Of(target) + " already has a Transform");
                }

                if (Attribute.IsDefined(type, typeof(DisallowMultipleComponent), true) && target.GetComponent(type) != null)
                {
                    return Answer.Error(Addresses.Of(target) + " already has a " + type.Name + ", which allows only one");
                }

                Component added = Addresses.ContentsPath(target) == null ? Undo.AddComponent(target, type) : target.AddComponent(type);
                return added == null
                    ? Answer.Error("Unity did not add " + type.Name + " to " + Addresses.Of(target) + ": ug console says why")
                    : Answer.Line("added " + Addresses.ComponentName(added) + " to " + Addresses.Of(target) + Answer.Unsaved(target));
            });
        }

        public static Reply RemoveComponent(Request request)
        {
            string name = request.Get("component");
            return Edit("component remove", request.Get("object"), target =>
            {
                if (string.Equals(name?.Trim(), "missing script", StringComparison.OrdinalIgnoreCase))
                {
                    return RemoveMissingScripts(target);
                }

                Component component = Addresses.Component(target, name, out string error);
                if (component == null)
                {
                    return Answer.Error(error);
                }

                if (component is Transform)
                {
                    return Answer.Error("the Transform cannot be removed: ug object delete " + Addresses.Of(target) + " removes the object");
                }

                string shown = Addresses.ComponentName(component);
                Component needing = RequiredBy(component);
                if (needing != null)
                {
                    return Answer.Error(shown + " is required by " + Addresses.ComponentName(needing) + " on " + Addresses.Of(target) + ": remove " + Addresses.ComponentName(needing) + " first");
                }

                Destroy(component);
                return Answer.Line("removed " + shown + " from " + Addresses.Of(target) + Answer.Unsaved(target));
            });
        }

        /// <summary>
        /// Sets one serialized field. On a game object without `component`, the field is looked up on
        /// the object (name, tag, layer, active, static) and then on its components.
        /// </summary>
        public static Reply Set(Request request)
        {
            string address = request.Get("object");
            string field = request.Get("field");
            string value = request.Get("value");
            if (value == null)
            {
                return Answer.Error("no value given");
            }

            string refused = ReadOnly(address);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            if (request.Get("importer") == "true")
            {
                return SetImporter(address.Trim(), field, value);
            }

            UnityEngine.Object target = Addresses.FindObject(address, out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            if (target is GameObject)
            {
                return Edit("set", address, go => SetOnObject(go, request.Get("component"), field, value));
            }

            if (target is Material material)
            {
                return Answer.Undoable("set", () => SetMaterial(material, field, value));
            }

            return Answer.Undoable("set", () => SetSerialized(target, field, value, AssetDatabase.GetAssetPath(target) + "  "));
        }

        public static Reply UndoLast(bool redo)
        {
            bool known = Records(out List<string> undos, out List<string> redos);
            List<string> records = redo ? redos : undos;
            if (known && records.Count == 0)
            {
                return Answer.Line(redo ? "nothing to redo" : "nothing to undo");
            }

            string name = known ? records[records.Count - 1] : null;
            if (redo)
            {
                Undo.PerformRedo();
            }
            else
            {
                Undo.PerformUndo();
            }

            return Answer.Line((redo ? "redid " : "undid ") + (string.IsNullOrEmpty(name) ? "the last change" : name));
        }

        public static Reply Select(Request request)
        {
            string address = request.Get("object");
            if (request.Get("clear") == "true")
            {
                Selection.objects = new UnityEngine.Object[0];
                return Answer.Line("nothing selected");
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                UnityEngine.Object[] selected = Selection.objects;
                var lines = selected.Take(MaxSelected).Select(Shown).ToList();
                if (selected.Length > MaxSelected)
                {
                    lines.Add("... " + Answer.Count(selected.Length - MaxSelected, "more", "more"));
                }

                return Answer.Lines(lines.Count == 0 ? new List<string> { "nothing selected" } : lines);
            }

            UnityEngine.Object target = Addresses.FindObject(address, out string error);
            if (target == null)
            {
                return Answer.Error(error);
            }

            Selection.activeObject = target;
            return Answer.Line("selected " + Shown(target));
        }

        private const string SlashName = "names with / cannot be addressed by ug: pick another name";

        // Resolves the object and runs the edit: in the loaded scenes as one undo group; inside a
        // prefab asset on its loaded contents, written when the edit worked (Safety rule 3).
        private static Reply Edit(string command, string address, Func<GameObject, Reply> edit)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return Answer.Error("no object given");
            }

            address = address.Trim();
            string refused = ReadOnly(address);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            if (Addresses.IsAssetPath(address) && Addresses.SplitPrefab(address, out string prefab, out string inner))
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(prefab) == null)
                {
                    return Answer.Error("not found: " + prefab);
                }

                GameObject root = Addresses.LoadContents(prefab);
                try
                {
                    string error = null;
                    GameObject inside = inner.Length == 0 ? root : Addresses.Walk(Addresses.Children(root), inner, prefab, out error);
                    if (inside == null)
                    {
                        return Answer.Error(error);
                    }

                    Reply reply = edit(inside);
                    if (!Answer.IsError(reply))
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, prefab);
                    }

                    return reply;
                }
                finally
                {
                    Addresses.UnloadContents();
                }
            }

            GameObject target = Addresses.Find(address, out string missing);
            return target == null ? Answer.Error(missing) : Answer.Undoable(command, () => edit(target));
        }

        // Safety rule 7: package contents are read-only. Vendored folders are refused by the client.
        private static string ReadOnly(string address) =>
            address != null && address.Trim().StartsWith("Packages/", StringComparison.Ordinal)
                ? address.Trim() + " is in a package, which ug does not edit: copy it into Assets/ first"
                : null;

        private static string Root(GameObject target, string verb, string instead)
        {
            string contents = Addresses.ContentsPath(target);
            if (contents == null || target.transform.parent != null)
            {
                return null;
            }

            return "the root of " + contents + " cannot be " + verb + (instead == null ? "" : ": " + instead + " " + contents);
        }

        // Objects that come from a prefab (not added in the scene) cannot be deleted or reparented.
        private static string InstancePart(GameObject target)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(target) || PrefabUtility.IsOutermostPrefabInstanceRoot(target)
                || PrefabUtility.IsAddedGameObjectOverride(target))
            {
                return null;
            }

            GameObject root = PrefabUtility.GetOutermostPrefabInstanceRoot(target);
            var inner = new List<string>();
            for (Transform t = target.transform; t != root.transform; t = t.parent)
            {
                inner.Add(Addresses.Segment(t.gameObject));
            }

            inner.Reverse();
            string asset = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            return Addresses.Of(target) + " is part of the prefab instance " + Addresses.Of(root) + ": edit " + asset + ":" + string.Join("/", inner)
                + ", or ug prefab unpack " + Addresses.Of(root) + " first";
        }

        // Another object next to the target: in the same prefab's contents when editing one.
        private static GameObject FindBeside(GameObject target, string address, out string error)
        {
            address = address.Trim();
            string contents = Addresses.ContentsPath(target);
            if (contents == null)
            {
                GameObject found = Addresses.Find(address, out error);
                if (found != null && EditorUtility.IsPersistent(found))
                {
                    error = "cannot move a scene object into a prefab asset: ug prefab create makes one";
                    return null;
                }

                return found;
            }

            if (!Addresses.SplitPrefab(address, out string prefab, out string inner) || prefab != contents)
            {
                error = "inside " + contents + " the parent must be in it too, like " + contents + ":Child";
                return null;
            }

            GameObject root = target.transform.root.gameObject;
            error = null;
            return inner.Length == 0 ? root : Addresses.Walk(Addresses.Children(root), inner, contents, out error);
        }

        // Unity's own Duplicate (Ctrl+D): keeps prefab links and overrides and places the copy
        // after the original. The user's selection is put back afterwards.
        private static GameObject DuplicateInScene(GameObject target)
        {
            UnityEngine.Object[] selection = Selection.objects;
            try
            {
                Selection.objects = new UnityEngine.Object[] { target };
                Unsupported.DuplicateGameObjectsUsingPasteboard();
                GameObject copy = Selection.activeGameObject;
                return copy == null || copy == target ? null : copy;
            }
            finally
            {
                Selection.objects = selection;
            }
        }

        private static Type ComponentType(string name, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "no component type given";
                return null;
            }

            name = name.Trim();
            List<Type> all = TypeCache.GetTypesDerivedFrom<Component>().Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition).ToList();
            List<Type> matches = all.Where(t => t.Name == name || t.FullName == name).ToList();
            if (matches.Count == 0)
            {
                matches = all.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            error = matches.Count > 1
                ? "ambiguous: " + name + " is " + string.Join(", ", matches.Select(t => t.FullName).OrderBy(n => n, StringComparer.Ordinal)) + ": give the full name"
                : "no component type " + name + Addresses.Near(name, all.Select(t => t.Name));
            return null;
        }

        private static Component RequiredBy(Component component)
        {
            Type type = component.GetType();
            Component[] all = component.GetComponents<Component>();
            foreach (Component other in all)
            {
                if (other == null || other == component)
                {
                    continue;
                }

                foreach (RequireComponent require in other.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    foreach (Type required in new[] { require.m_Type0, require.m_Type1, require.m_Type2 })
                    {
                        if (required != null && required.IsAssignableFrom(type)
                            && !all.Any(c => c != null && c != component && required.IsInstanceOfType(c)))
                        {
                            return other;
                        }
                    }
                }
            }

            return null;
        }

        private static Reply RemoveMissingScripts(GameObject target)
        {
            if (Addresses.ContentsPath(target) == null)
            {
                Undo.RegisterCompleteObjectUndo(target, Answer.GroupName);
            }

            int removed = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(target);
            return removed == 0
                ? Answer.Error(Addresses.Of(target) + " has no missing scripts")
                : Answer.Line("removed " + Answer.Count(removed, "missing script", "missing scripts") + " from " + Addresses.Of(target) + Answer.Unsaved(target));
        }

        private static Reply SetOnObject(GameObject target, string componentName, string field, string value)
        {
            UnityEngine.Object owner;
            string error;
            if (!string.IsNullOrWhiteSpace(componentName))
            {
                owner = Addresses.Component(target, componentName, out error);
            }
            else
            {
                owner = FieldOwner(target, field, out error);
            }

            if (owner == null)
            {
                return Answer.Error(error);
            }

            string prefix = Addresses.Of(target) + "  " + (owner is Component component ? Addresses.ComponentName(component) + "." : "");
            return SetSerialized(owner, field, value, prefix);
        }

        // The game object itself when it has the field, else the one component that has it.
        private static UnityEngine.Object FieldOwner(GameObject target, string field, out string error)
        {
            if (Values.Find(new SerializedObject(target), field, out error) != null)
            {
                return target;
            }

            List<Component> owners = target.GetComponents<Component>()
                .Where(c => c != null && Values.Find(new SerializedObject(c), field, out _) != null)
                .ToList();
            if (owners.Count == 1)
            {
                error = null;
                return owners[0];
            }

            string components = string.Join(", ", target.GetComponents<Component>().Select(Addresses.ComponentName));
            error = owners.Count > 1
                ? "ambiguous: " + field + " is on " + string.Join(", ", owners.Select(Addresses.ComponentName)) + ": give --component"
                : "no field " + field + " on " + Addresses.Of(target) + " or its components (" + components + ")";
            return null;
        }

        private static Reply SetSerialized(UnityEngine.Object target, string field, string value, string prefix)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty p = Values.Find(serialized, field, out string error);
            if (p == null)
            {
                return Answer.Error(error);
            }

            if (target is GameObject && p.name == "m_TagString" && !InternalEditorUtility.tags.Contains(value.Trim()))
            {
                return Answer.Error("no tag " + value.Trim() + " (tags: " + string.Join(", ", InternalEditorUtility.tags) + ")");
            }

            if (target is GameObject && p.name == "m_Layer" && !int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                int layer = LayerMask.NameToLayer(value.Trim());
                if (layer < 0)
                {
                    return Answer.Error("no layer " + value.Trim());
                }

                value = layer.ToString(CultureInfo.InvariantCulture);
            }

            string shown = field.IndexOf('.') >= 0 || field.IndexOf('[') >= 0 ? field.Trim() : Values.ShownName(p.name);
            string before = Values.Format(p);
            string problem = Values.Parse(p, value);
            if (problem != null)
            {
                return Answer.Error(prefix.TrimEnd() + (prefix.EndsWith(".", StringComparison.Ordinal) ? "" : " ") + shown + ": " + problem);
            }

            GameObject owner = target as GameObject ?? (target as Component)?.gameObject;
            if (Addresses.ContentsPath(owner) != null)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                serialized.ApplyModifiedProperties();
                if (owner == null && EditorUtility.IsPersistent(target))
                {
                    EditorUtility.SetDirty(target);
                }
            }

            serialized.Update();
            string suffix = owner != null ? Answer.Unsaved(owner) : Answer.UnsavedAsset(target);
            return Answer.Line(prefix + shown + "  " + before + " -> " + Values.Format(p) + suffix);
        }

        // Material properties by shader property name (`_Color`, or `color`), and `shader`.
        private static Reply SetMaterial(Material material, string field, string value)
        {
            string path = AssetDatabase.GetAssetPath(material);
            field = (field ?? "").Trim();
            if (field == "shader")
            {
                Shader shader = Shader.Find(value.Trim());
                if (shader == null)
                {
                    return Answer.Error("no shader " + value.Trim());
                }

                string was = material.shader == null ? "missing" : material.shader.name;
                Undo.RecordObject(material, Answer.GroupName);
                material.shader = shader;
                EditorUtility.SetDirty(material);
                return Answer.Line(path + "  shader  " + was + " -> " + shader.name + Answer.UnsavedAsset(material));
            }

            Shader current = material.shader;
            string name = new[] { field, "_" + field, field.Length > 0 ? "_" + char.ToUpperInvariant(field[0]) + field.Substring(1) : field }
                .FirstOrDefault(n => n.Length > 0 && current.FindPropertyIndex(n) >= 0);
            if (name == null)
            {
                var names = Enumerable.Range(0, current.GetPropertyCount()).Select(current.GetPropertyName).Take(20);
                return Answer.Error("no property " + field + " on " + path + " (shader " + current.name + ": " + string.Join(", ", names) + ")");
            }

            ShaderPropertyType type = current.GetPropertyType(current.FindPropertyIndex(name));
            string before = ValueOf(ReadOps.MaterialProperty(material, type, name));
            string text = value.Trim();
            Action set;
            switch (type)
            {
                case ShaderPropertyType.Color:
                    if (!Values.TryColor(text, out Color color))
                    {
                        return Answer.Error("not a color (#ff8800, #ff8800cc or 1, 0.5, 0, 1): " + value);
                    }

                    set = () => material.SetColor(name, color);
                    break;
                case ShaderPropertyType.Vector:
                    if (!Values.TryNumbers(text, 4, out float[] v))
                    {
                        return Answer.Error("not 4 numbers like 1, 2, 3, 4: " + value);
                    }

                    set = () => material.SetVector(name, new Vector4(v[0], v[1], v[2], v[3]));
                    break;
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                    {
                        return Answer.Error("not a number: " + value);
                    }

                    set = () => material.SetFloat(name, number);
                    break;
                case ShaderPropertyType.Int:
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole))
                    {
                        return Answer.Error("not a whole number: " + value);
                    }

                    set = () => material.SetInteger(name, whole);
                    break;
                case ShaderPropertyType.Texture:
                    Texture texture = null;
                    if (text != "null" && text.Length > 0)
                    {
                        texture = AssetDatabase.LoadAssetAtPath<Texture>(text);
                        if (texture == null)
                        {
                            return Answer.Error("no texture at " + text);
                        }
                    }

                    set = () => material.SetTexture(name, texture);
                    break;
                default:
                    return Answer.Error("ug set cannot set a " + type + " material property");
            }

            Undo.RecordObject(material, Answer.GroupName);
            set();
            EditorUtility.SetDirty(material);
            return Answer.Line(path + "  " + name + "  " + before + " -> " + ValueOf(ReadOps.MaterialProperty(material, type, name)) + Answer.UnsavedAsset(material));
        }

        // Importer settings live in the .meta file: applied and reimported at once.
        private static Reply SetImporter(string path, string field, string value)
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                return Answer.Error(AssetDatabase.LoadMainAssetAtPath(path) == null ? "not found: " + path : path + " has no importer settings");
            }

            var serialized = new SerializedObject(importer);
            SerializedProperty p = Values.Find(serialized, field, out string error);
            if (p == null)
            {
                return Answer.Error(error);
            }

            string shown = Values.ShownName(p.name);
            string before = Values.Format(p);
            string problem = Values.Parse(p, value);
            if (problem != null)
            {
                return Answer.Error(path + " " + shown + ": " + problem);
            }

            string propertyPath = p.propertyPath;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
            SerializedProperty after = new SerializedObject(AssetImporter.GetAtPath(path)).FindProperty(propertyPath);
            return Answer.Line(path + "  " + importer.GetType().Name + "." + shown + "  " + before + " -> " + Values.Format(after) + "  (reimported; .meta written)");
        }

        // `Color  1, 0, 0, 1` to `1, 0, 0, 1`.
        private static string ValueOf(string typed)
        {
            int gap = typed.IndexOf("  ", StringComparison.Ordinal);
            return gap < 0 ? typed : typed.Substring(gap + 2);
        }

        private static void Record(UnityEngine.Object target)
        {
            GameObject owner = target as GameObject ?? (target as Component)?.gameObject;
            if (Addresses.ContentsPath(owner) == null)
            {
                Undo.RecordObject(target, Answer.GroupName);
            }
        }

        private static void Destroy(UnityEngine.Object target)
        {
            GameObject owner = target as GameObject ?? (target as Component)?.gameObject;
            if (Addresses.ContentsPath(owner) == null)
            {
                Undo.DestroyObjectImmediate(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static Scene FindScene(string name)
        {
            name = name.Trim();
            return Addresses.LoadedScenes().FirstOrDefault(s => s.path == name || s.name == name);
        }

        private static string Shown(UnityEngine.Object target)
        {
            if (target is GameObject go)
            {
                return Addresses.Of(go);
            }

            string path = AssetDatabase.GetAssetPath(target);
            return string.IsNullOrEmpty(path) ? target.name + " (" + target.GetType().Name + ")" : path;
        }

        private static string Bool(bool value) => value ? "true" : "false";

        // Unity keeps the undo and redo names internally (Edit > Undo shows them); read them by
        // reflection, and fall back to nameless answers when that method is gone.
        private static bool Records(out List<string> undos, out List<string> redos)
        {
            undos = new List<string>();
            redos = new List<string>();
            MethodInfo method = typeof(Undo).GetMethod(
                "GetRecords",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(List<string>), typeof(List<string>) },
                null);
            if (method == null)
            {
                return false;
            }

            try
            {
                method.Invoke(null, new object[] { undos, redos });
                return true;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
        }
    }
}
