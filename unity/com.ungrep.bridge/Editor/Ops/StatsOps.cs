using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace Ungrep.Bridge
{
    /// <summary>
    /// `/stats` (`ug perf`, SPEC-editor-ops, Endpoints): render stats of the Game view (play mode),
    /// memory, and counts of scene objects and assets. Read-only, a handful of lines.
    /// </summary>
    internal static class StatsOps
    {
        public static Reply Stats()
        {
            var lines = new List<string>
            {
                SceneOps.PlayState(),
                Render(),
                Memory(),
                Objects(),
                Assets(),
            };
            return Answer.Lines(lines);
        }

        // UnityStats is the Stats overlay of the Game view: it only counts while a Game view renders in play mode.
        private static string Render() =>
            EditorApplication.isPlaying
                ? "render  " + Number(UnityStats.drawCalls) + " draw calls  " + Number(UnityStats.batches) + " batches  "
                    + Number(UnityStats.setPassCalls) + " set-pass calls  " + Number(UnityStats.triangles) + " triangles  "
                    + Number(UnityStats.vertices) + " vertices  " + Number(UnityStats.shadowCasters) + " shadow casters  "
                    + UnityStats.frameTime.ToString("0.0", CultureInfo.InvariantCulture) + " ms frame  "
                    + UnityStats.renderTime.ToString("0.0", CultureInfo.InvariantCulture) + " ms render"
                : "render  only counted in play mode (ug play, then ug perf)";

        private static string Memory() =>
            "memory  " + Megabytes(Profiler.GetTotalAllocatedMemoryLong()) + " allocated of " + Megabytes(Profiler.GetTotalReservedMemoryLong())
            + " reserved  " + Megabytes(Profiler.GetMonoUsedSizeLong()) + " scripts heap  " + Megabytes(Profiler.GetAllocatedMemoryForGraphicsDriver()) + " graphics driver";

        private static string Objects()
        {
            int scenes = 0;
            int objects = 0;
            int active = 0;
            int components = 0;
            foreach (Scene scene in Addresses.LoadedScenes())
            {
                scenes++;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    Transform[] all = root.GetComponentsInChildren<Transform>(true);
                    objects += all.Length;
                    active += all.Count(t => t.gameObject.activeInHierarchy);
                    components += root.GetComponentsInChildren<Component>(true).Length;
                }
            }

            return "objects  " + Number(objects) + " game objects (" + Number(active) + " active), " + Number(components) + " components in "
                + Answer.Count(scenes, "loaded scene", "loaded scenes");
        }

        private static string Assets()
        {
            string[] roots = { "Assets" };
            int files = AssetDatabase.FindAssets("", roots).Count(guid => !AssetDatabase.IsValidFolder(AssetDatabase.GUIDToAssetPath(guid)));
            string kinds = string.Join(", ", new[] { "Scene", "Prefab", "Material", "Texture", "Script", "ScriptableObject" }
                .Select(type => new { type, count = AssetDatabase.FindAssets("t:" + type, roots).Length })
                .Where(entry => entry.count > 0)
                .Select(entry => Number(entry.count) + " " + entry.type.ToLowerInvariant() + "s"));
            return "assets  " + Number(files) + " files in Assets/" + (kinds.Length == 0 ? "" : ": " + kinds);
        }

        private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }
}
