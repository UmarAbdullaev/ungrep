using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Asset requests (SPEC-editor-ops, Endpoints and Safety rule 9): folders, materials and
    /// ScriptableObjects, move, rename, duplicate, delete to the OS trash and reimport, all through
    /// AssetDatabase so `.meta` files and GUIDs follow. They write at once and Unity has no undo for
    /// them, so no undo group is made and each answer says `not undoable`. Packages and vendored
    /// folders are refused (rule 7).
    /// </summary>
    internal static class AssetOps
    {
        /// <summary>`/asset/folder?path=`: a folder, with any missing folders above it.</summary>
        public static Reply Folder(Request request)
        {
            string path = Clean(request.Get("path"));
            string refused = Target(request, path, "path");
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            return MakeFolders(path) ?? Answer.Line("created folder " + path + Written(path));
        }

        /// <summary>`/asset/create?path=&amp;type=&amp;shader=`: a folder, a material (`type=Material`) or a ScriptableObject by type name.</summary>
        public static Reply Create(Request request)
        {
            string path = Clean(request.Get("path"));
            string type = (request.Get("type") ?? "").Trim();
            if (type.Length == 0)
            {
                return Answer.Error("no type given (Folder, Material, or a ScriptableObject type)");
            }

            if (string.Equals(type, "Folder", StringComparison.OrdinalIgnoreCase))
            {
                return Folder(request);
            }

            string refused = Target(request, path, "path");
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            bool material = string.Equals(type, "Material", StringComparison.OrdinalIgnoreCase);
            string extension = material ? ".mat" : ".asset";
            if (!path.EndsWith(extension, StringComparison.Ordinal))
            {
                return Answer.Error("a " + (material ? "material" : "ScriptableObject") + " path must end in " + extension + ": " + path);
            }

            string folder = path.Substring(0, path.LastIndexOf('/'));
            if (!AssetDatabase.IsValidFolder(folder))
            {
                return Answer.Error("the folder " + folder + " does not exist: ug asset folder " + folder + " makes it");
            }

            UnityEngine.Object made;
            string kind;
            if (material)
            {
                string named = request.Get("shader");
                Shader shader = string.IsNullOrWhiteSpace(named) ? DefaultShader() : Shader.Find(named.Trim());
                if (shader == null)
                {
                    return Answer.Error(string.IsNullOrWhiteSpace(named)
                        ? "no default shader for this render pipeline: give --shader"
                        : "no shader " + named.Trim() + Addresses.Near(named.Trim(), ShaderUtil.GetAllShaderInfo().Select(s => s.name)));
                }

                made = new Material(shader);
                kind = "Material, shader " + shader.name;
            }
            else
            {
                Type scriptable = ScriptableType(type, out string error);
                if (scriptable == null)
                {
                    return Answer.Error(error);
                }

                made = ScriptableObject.CreateInstance(scriptable);
                if (made == null)
                {
                    return Answer.Error("Unity could not create a " + scriptable.Name + ": ug console says why");
                }

                kind = scriptable.FullName;
            }

            AssetDatabase.CreateAsset(made, path);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets)))
            {
                UnityEngine.Object.DestroyImmediate(made);
                return Answer.Error("Unity did not create " + path + ": ug console says why");
            }

            return Answer.Line("created " + path + " (" + kind + ")" + Written(path));
        }

        /// <summary>`/asset/move?from=&amp;to=`: an asset or folder moved, its GUID kept.</summary>
        public static Reply Move(Request request) =>
            Relocate(request, "moved", Clean(request.Get("from")), Clean(request.Get("to")));

        /// <summary>`/asset/rename?path=&amp;name=`: a move within its folder; the file extension stays.</summary>
        public static Reply Rename(Request request)
        {
            string path = Clean(request.Get("path"));
            string name = (request.Get("name") ?? "").Trim();
            if (name.Length == 0)
            {
                return Answer.Error("no new name given");
            }

            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
            {
                return Answer.Error("a new name has no folder: ug asset move " + path + " <path> moves it");
            }

            int slash = path.LastIndexOf('/');
            string extension = AssetDatabase.IsValidFolder(path) ? "" : Path.GetExtension(path);
            if (extension.Length > 0 && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && name.Length > extension.Length)
            {
                name = name.Substring(0, name.Length - extension.Length);
            }

            return Relocate(request, "renamed", path, (slash < 0 ? "" : path.Substring(0, slash + 1)) + name + extension);
        }

        /// <summary>`/asset/duplicate?path=&amp;to=`: a copy with a new GUID. A package asset may be copied into Assets/.</summary>
        public static Reply Duplicate(Request request)
        {
            string path = Clean(request.Get("path"));
            string to = Clean(request.Get("to"));
            if (path.Length == 0)
            {
                return Answer.Error("no path given");
            }

            if (path == "Assets")
            {
                return Answer.Error("Assets/ itself cannot be duplicated");
            }

            bool package = path.StartsWith("Packages/", StringComparison.Ordinal);
            string bad = PathProblem(path, allowPackages: true);
            if (bad != null)
            {
                return Answer.Error(bad);
            }

            if (!Exists(path))
            {
                return Answer.Error("not found: " + path);
            }

            if (to.Length == 0)
            {
                if (package)
                {
                    return Answer.Error(path + " is in a package: give a destination under Assets/");
                }

                to = AssetDatabase.GenerateUniqueAssetPath(path);
            }

            string refused = Target(request, to, "destination");
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            bool folder = AssetDatabase.IsValidFolder(path);
            if (folder && (to + "/").StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
            {
                return Answer.Error("cannot copy " + path + " into itself");
            }

            refused = SameKind(path, to, folder) ?? FolderMissing(to);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            if (!AssetDatabase.CopyAsset(path, to))
            {
                return Answer.Error("Unity did not copy " + path + " to " + to + ": ug console says why");
            }

            return Answer.Line("duplicated " + path + " -> " + to + Written(to, "new guid " + AssetDatabase.AssetPathToGUID(to)));
        }

        /// <summary>`/asset/delete?path=`: an asset or folder moved to the OS trash. Never a permanent delete.</summary>
        public static Reply Delete(Request request)
        {
            string path = Clean(request.Get("path"));
            string refused = Source(request, path, "path", "deleted");
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            string open = Addresses.LoadedScenes()
                .Select(s => s.path)
                .FirstOrDefault(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal));
            if (open != null)
            {
                return Answer.Error(path + (open == path ? " is an open scene" : " holds the open scene " + open) + ": ug scene close " + open + " first");
            }

            if (!AssetDatabase.MoveAssetToTrash(path))
            {
                return Answer.Error("Unity did not move " + path + " to the trash: ug console says why");
            }

            return Answer.Line("deleted " + path + "  (moved to the OS trash with its .meta; not undoable)");
        }

        /// <summary>`/asset/reimport?path=`: forces an import (a folder: everything under it).</summary>
        public static Reply Reimport(Request request)
        {
            string path = Clean(request.Get("path"));
            string refused = Source(request, path, "path", "reimported");
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            bool folder = AssetDatabase.IsValidFolder(path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | (folder ? ImportAssetOptions.ImportRecursive : ImportAssetOptions.Default));
            return Answer.Line("reimported " + path + (folder ? " and everything under it" : "") + "  (import results are in Library/; its .meta is rewritten only if Unity changes a setting)");
        }

        private static Reply Relocate(Request request, string verb, string from, string to)
        {
            string refused = Source(request, from, "path", verb) ?? Target(request, to, "destination");
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            bool folder = AssetDatabase.IsValidFolder(from);
            if (folder && (to + "/").StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
            {
                return Answer.Error("cannot move " + from + " into itself");
            }

            refused = SameKind(from, to, folder) ?? FolderMissing(to);
            if (refused != null)
            {
                return Answer.Error(refused);
            }

            string invalid = AssetDatabase.ValidateMoveAsset(from, to);
            if (!string.IsNullOrEmpty(invalid))
            {
                return Answer.Error(invalid);
            }

            string guid = AssetDatabase.AssetPathToGUID(from, AssetPathToGUIDOptions.OnlyExistingAssets);
            invalid = AssetDatabase.MoveAsset(from, to);
            if (!string.IsNullOrEmpty(invalid))
            {
                return Answer.Error(invalid);
            }

            return Answer.Line(verb + " " + from + " -> " + to + Written(to, "guid " + guid + " kept"));
        }

        // An existing asset or folder this request changes: the path checks, and not Assets/ itself.
        private static string Source(Request request, string path, string what, string verb)
        {
            if (path.Length == 0)
            {
                return "no " + what + " given";
            }

            if (path == "Assets")
            {
                return "Assets/ itself cannot be " + verb;
            }

            string refused = PrefabOps.Vendored(request, path, true) ?? PathProblem(path);
            if (refused != null)
            {
                return refused;
            }

            return Exists(path) ? null : "not found: " + path;
        }

        // A path this request creates: the path checks, and nothing there yet.
        private static string Target(Request request, string path, string what)
        {
            if (path.Length == 0)
            {
                return "no " + what + " given";
            }

            string refused = PrefabOps.Vendored(request, path) ?? PathProblem(path);
            if (refused != null)
            {
                return refused;
            }

            return Exists(path) ? path + " already exists: ug does not overwrite it" : null;
        }

        private static string PathProblem(string path, bool allowPackages = false) =>
            !(path.StartsWith("Assets/", StringComparison.Ordinal) || allowPackages && path.StartsWith("Packages/", StringComparison.Ordinal)) || path.IndexOf('\\') >= 0 || path.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")
                ? "an asset path must be a project path under Assets/, like Assets/Art/Hero.png: " + path
                : null;

        private static string SameKind(string from, string to, bool folder) =>
            !folder && !string.Equals(Path.GetExtension(from), Path.GetExtension(to), StringComparison.OrdinalIgnoreCase)
                ? "the extension must stay " + Path.GetExtension(from) + ": ug does not convert assets (" + to + ")"
                : null;

        private static string FolderMissing(string path)
        {
            string folder = path.Substring(0, path.LastIndexOf('/'));
            return AssetDatabase.IsValidFolder(folder) ? null : "the folder " + folder + " does not exist: ug asset folder " + folder + " makes it";
        }

        // Folders down to `path`, each created by AssetDatabase; null when done, else the refusal.
        private static Reply MakeFolders(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (Exists(next))
                    {
                        return Answer.Error(next + " is a file, not a folder");
                    }

                    if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(current, parts[i])))
                    {
                        return Answer.Error("Unity did not create the folder " + next + ": ug console says why");
                    }
                }

                current = next;
            }

            return null;
        }

        private static bool Exists(string path) =>
            AssetDatabase.IsValidFolder(path)
            || !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets))
            || File.Exists(path)
            || Directory.Exists(path);

        private static string Clean(string path) => (path ?? "").Trim().TrimEnd('/');

        // `  (written Assets/X.mat; guid ... kept; not undoable)`.
        private static string Written(string path, string also = null) =>
            "  (written " + path + "; " + (also == null ? "" : also + "; ") + "not undoable)";

        // The render pipeline's default material shader, else the built-in Standard.
        private static Shader DefaultShader() =>
            GraphicsSettings.currentRenderPipeline?.defaultMaterial?.shader ?? Shader.Find("Standard");

        private static Type ScriptableType(string name, out string error)
        {
            error = null;
            List<Type> all = TypeCache.GetTypesDerivedFrom<ScriptableObject>()
                .Where(t => !t.IsAbstract && !t.IsGenericTypeDefinition && !typeof(EditorWindow).IsAssignableFrom(t) && !typeof(Editor).IsAssignableFrom(t))
                .ToList();
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
                : "no Material, Folder or ScriptableObject type " + name + Addresses.Near(name, all.Select(t => t.Name));
            return null;
        }
    }
}
