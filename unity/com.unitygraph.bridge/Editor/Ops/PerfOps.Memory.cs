using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityGraph.Bridge
{
    /// <summary>`ug perf memory` and `ug perf snapshot take` (SPEC-editor-ops, Profiler).</summary>
    internal static partial class PerfOps
    {
        private const string SnapshotFolder = "Library/unitygraph/snapshots";

        /// <summary>`/perf/memory?top=&amp;type=&amp;object=`: loaded objects by native size, or one object.</summary>
        public static Reply Memory(Request request)
        {
            if (!Whole(request, "top", 1, 500, out int? given, out Reply bad))
            {
                return bad;
            }

            string address = request.Get("object");
            if (address != null)
            {
                return MemoryOf(address);
            }

            int top = given ?? 10;
            string typeName = request.Get("type");
            Type type = typeof(Object);
            if (typeName != null)
            {
                type = ObjectType(typeName);
                if (type == null)
                {
                    return Answer.Error("no UnityEngine.Object type named " + typeName);
                }
            }

            var sized = Resources.FindObjectsOfTypeAll<Object>()
                .Where(o => o != null && type.IsInstanceOfType(o))
                .Select(o => new KeyValuePair<Object, long>(o, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(o)))
                .ToList();
            long total = sized.Sum(s => s.Value);
            var lines = new List<string>
            {
                (typeName == null ? "" : type.Name + " and subclasses: ") + Number(sized.Count) + " objects, " + Bytes(total)
                    + " (native sizes, Profiler.GetRuntimeMemorySizeLong; " + SceneOps.PlayState() + ")",
            };
            var types = sized.GroupBy(s => s.Key.GetType().Name)
                .Select(g => new { Name = g.Key, Size = g.Sum(s => s.Value), Count = g.Count() })
                .OrderByDescending(g => g.Size).ToList();
            lines.Add("by type  " + string.Join(", ", types.Take(8).Select(t => t.Name + " " + Bytes(t.Size) + " (" + Number(t.Count) + ")"))
                + (types.Count > 8 ? ", +" + Answer.Count(types.Count - 8, "type", "types") + " (ug perf memory --type <T>)" : ""));
            lines.Add("top objects:");
            var largest = sized.OrderByDescending(s => s.Value).ToList();
            lines.AddRange(largest.Take(top).Select(s => "  " + Bytes(s.Value) + "  " + s.Key.GetType().Name + "  " + Name(s.Key) + "  " + Where(s.Key)));
            if (largest.Count > top)
            {
                lines.Add("  ... " + Number(largest.Count - top) + " more: ug perf memory --top " + Math.Min(500, top * 3)
                    + (typeName == null ? "" : " --type " + typeName));
            }

            return Answer.Lines(lines);
        }

        // One object: a game object with its components, children and the assets it uses; a
        // component; or an asset path with its sub-assets.
        private static Reply MemoryOf(string address)
        {
            address = address.Trim();
            if (Addresses.IsAssetPath(address) && address.IndexOf(Addresses.PrefabSeparator, StringComparison.Ordinal) < 0)
            {
                Object[] parts = AssetDatabase.LoadAllAssetsAtPath(address).Where(o => o != null).ToArray();
                if (parts.Length == 0)
                {
                    return Answer.Error("not found: " + address);
                }

                var sizes = parts.Select(o => new KeyValuePair<Object, long>(o, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(o)))
                    .OrderByDescending(s => s.Value).ToList();
                var lines = new List<string> { address + "  " + Bytes(sizes.Sum(s => s.Value)) + " in " + Answer.Count(sizes.Count, "object", "objects") + " (loaded to measure)" };
                lines.AddRange(sizes.Take(20).Select(s => "  " + Bytes(s.Value) + "  " + s.Key.GetType().Name + "  " + Name(s.Key)
                    + (AssetDatabase.IsMainAsset(s.Key) ? "  (main)" : "")));
                if (sizes.Count > 20)
                {
                    lines.Add("  ... " + Number(sizes.Count - 20) + " more sub-assets");
                }

                lines.AddRange(Uses(parts, address));
                return Answer.Lines(lines);
            }

            Object found = Addresses.FindObject(address, out string error);
            if (found == null)
            {
                int colon = address.LastIndexOf(':');
                GameObject holder = colon > 0 ? Addresses.Find(address.Substring(0, colon), out _) : null;
                found = holder == null ? null : Addresses.Component(holder, address.Substring(colon + 1), out _);
                if (found == null)
                {
                    return Answer.Error(error);
                }
            }

            if (!(found is GameObject target))
            {
                var lines = new List<string> { address + "  " + found.GetType().Name + "  " + Bytes(UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(found)) };
                lines.AddRange(Uses(new[] { found }, AssetDatabase.GetAssetPath(found)));
                return Answer.Lines(lines);
            }

            Component[] components = target.GetComponents<Component>().Where(c => c != null).ToArray();
            var parts2 = components.Select(c => new KeyValuePair<string, long>(Addresses.ComponentName(c), UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(c)))
                .OrderByDescending(p => p.Value).ToList();
            var result = new List<string>
            {
                Addresses.Of(target) + "  GameObject  " + Bytes(UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(target)) + "; components "
                    + Bytes(parts2.Sum(p => p.Value)) + ": " + string.Join(", ", parts2.Select(p => p.Key + " " + Bytes(p.Value))),
            };
            Transform[] below = target.GetComponentsInChildren<Transform>(true).Where(t => t != target.transform).ToArray();
            if (below.Length > 0)
            {
                long children = below.Sum(t => UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t.gameObject)
                    + t.GetComponents<Component>().Where(c => c != null).Sum(c => UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(c)));
                result.Add("children  " + Answer.Count(below.Length, "object", "objects") + ", " + Bytes(children) + " with their components");
            }

            result.AddRange(Uses(new Object[] { target }, EditorUtility.IsPersistent(target) ? AssetDatabase.GetAssetPath(target) : null));
            return Answer.Lines(result);
        }

        // The assets an object uses (meshes, materials, textures, ...), largest first; not its own asset or scripts.
        private static IEnumerable<string> Uses(Object[] roots, string own)
        {
            var used = EditorUtility.CollectDependencies(roots)
                .Where(o => o != null && EditorUtility.IsPersistent(o) && !(o is MonoScript) && !(o is GameObject) && !(o is Component)
                    && (string.IsNullOrEmpty(own) || AssetDatabase.GetAssetPath(o) != own))
                .Distinct()
                .Select(o => new KeyValuePair<Object, long>(o, UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(o)))
                .OrderByDescending(s => s.Value).ToList();
            if (used.Count == 0)
            {
                yield break;
            }

            yield return "uses  " + Answer.Count(used.Count, "asset", "assets") + ", " + Bytes(used.Sum(s => s.Value)) + ": "
                + string.Join(", ", used.Take(5).Select(s => s.Key.GetType().Name + " " + Name(s.Key) + " " + Bytes(s.Value) + " (" + AssetDatabase.GetAssetPath(s.Key) + ")"))
                + (used.Count > 5 ? ", +" + Number(used.Count - 5) : "");
        }

        private static Type ObjectType(string name)
        {
            List<Type> types = TypeCache.GetTypesDerivedFrom<Object>().Append(typeof(Object)).ToList();
            return types.FirstOrDefault(t => t.FullName == name) ?? types.FirstOrDefault(t => t.Name == name)
                ?? types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private static string Name(Object target) => string.IsNullOrEmpty(target.name) ? "(no name)" : target.name;

        // Where an object lives: its asset, its scene address, the editor's or engine's hidden objects, or nowhere.
        private static string Where(Object target)
        {
            if (EditorUtility.IsPersistent(target))
            {
                string path = AssetDatabase.GetAssetPath(target);
                return string.IsNullOrEmpty(path) ? "built in" : path;
            }

            GameObject owner = target as GameObject ?? (target as Component)?.gameObject;
            if (owner != null && owner.scene.IsValid() && (owner.hideFlags & HideFlags.HideInHierarchy) == 0)
            {
                return Addresses.Of(owner) + (target is Component component ? ":" + Addresses.ComponentName(component) : "");
            }

            return (target.hideFlags & (HideFlags.DontSave | HideFlags.HideInHierarchy)) != 0 ? "hidden" : "not saved";
        }

        /// <summary>`/perf/snapshot?path=`: a memory snapshot through Unity's own API, without the package.</summary>
        public static Func<Reply> Snapshot(Request request)
        {
            string path = OutputPath(request.Get("path") ?? "", SnapshotFolder, ".snap", out string error);
            if (path == null)
            {
                return Now(Answer.Error(error));
            }

            bool done = false;
            bool ok = false;
            double calledBack = 0;
            var clock = Stopwatch.StartNew();
            string mode = SceneOps.PlayState();
            Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot(path, (written, success) =>
            {
                done = true;
                ok = success;
            }, Unity.Profiling.Memory.CaptureFlags.ManagedObjects | Unity.Profiling.Memory.CaptureFlags.NativeObjects | Unity.Profiling.Memory.CaptureFlags.NativeAllocations);
            return () =>
            {
                if (!done)
                {
                    return clock.Elapsed.TotalSeconds < 60 ? null : Answer.Error("Unity did not finish the snapshot in 60 s (" + Shown(path) + ")");
                }

                if (!ok || !File.Exists(path))
                {
                    return Answer.Error("Unity could not take the snapshot to " + Shown(path));
                }

                // Unity can call back before its writer has flushed the file: wait for the end
                // signature the client's reader checks.
                if (!Written(path))
                {
                    calledBack = calledBack > 0 ? calledBack : clock.Elapsed.TotalSeconds;
                    return clock.Elapsed.TotalSeconds - calledBack < 20 ? null
                        : Answer.Error("Unity said the snapshot was done, but " + Shown(path) + " has no end 20 s later (" + FileSize(path) + ")");
                }

                return Answer.Line(Shown(path) + "  " + FileSize(path) + "  in " + Seconds(clock.Elapsed.TotalSeconds) + " s (" + mode + "); ug perf snapshot list, ug perf snapshot compare <a> <b>");
            };
        }

        // A snapshot file ends with 0xABCDCDAE after its directory offset.
        private static bool Written(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var tail = new byte[4];
                    if (stream.Length < 16)
                    {
                        return false;
                    }

                    stream.Seek(-4, SeekOrigin.End);
                    return stream.Read(tail, 0, 4) == 4 && BitConverter.ToUInt32(tail, 0) == 0xABCDCDAE;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }

        // A file ug writes: `given` relative to the project or absolute, with `extension` added
        // when it has none, or a timestamped file in `folder`. Refuses Assets/, Packages/, an
        // existing file and a missing folder.
        private static string OutputPath(string given, string folder, string extension, out string error)
        {
            error = null;
            string root = Path.GetFullPath(Bridge.ProjectRoot);
            if (string.IsNullOrWhiteSpace(given))
            {
                string directory = Path.Combine(root, folder);
                Directory.CreateDirectory(directory);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string path = Path.Combine(directory, stamp + extension);
                for (int i = 2; File.Exists(path); i++)
                {
                    path = Path.Combine(directory, stamp + "-" + i + extension);
                }

                return path;
            }

            string full = Path.GetFullPath(Path.Combine(root, given.Trim()));
            if (Path.GetExtension(full).Length == 0)
            {
                full += extension;
            }
            else if (!string.Equals(Path.GetExtension(full), extension, StringComparison.OrdinalIgnoreCase))
            {
                error = "give a " + extension + " path: " + given;
                return null;
            }

            string shown = Shown(full);
            foreach (string place in new[] { "Assets", "Packages" })
            {
                string inside = Path.Combine(root, place) + Path.DirectorySeparatorChar;
                if (full.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
                {
                    error = shown + " is under " + place + "/, where Unity would import it: use Library/ or a folder outside the project";
                    return null;
                }
            }

            if (File.Exists(full))
            {
                error = shown + " exists; ug does not overwrite it";
                return null;
            }

            if (!Directory.Exists(Path.GetDirectoryName(full)))
            {
                error = "no folder " + Shown(Path.GetDirectoryName(full));
                return null;
            }

            return full;
        }

        // A path relative to the project with forward slashes when inside it, else as it is.
        private static string Shown(string path)
        {
            string root = Path.GetFullPath(Bridge.ProjectRoot).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full).Replace('\\', '/');
        }
    }
}
