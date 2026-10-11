using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ungrep.Bridge
{
    /// <summary>
    /// `/project` (SPEC-editor-ops, Endpoints): tags, layers, sorting layers, build scenes, the Unity
    /// version and the render pipeline. Read-only; changing them is `ug set` on `ProjectSettings/*.asset`.
    /// </summary>
    internal static class ProjectOps
    {
        public static Reply Project()
        {
            var lines = new List<string>
            {
                "Unity " + Application.unityVersion + "  " + Pipeline(),
                "tags  " + string.Join(", ", InternalEditorUtility.tags),
                "layers  " + string.Join(", ", Layers()),
                "sorting layers  " + string.Join(", ", SortingLayer.layers.Select(layer => layer.name)),
            };
            lines.AddRange(BuildScenes(EditorBuildSettings.scenes));
            return Answer.Lines(lines);
        }

        // The layers Unity has named, as `index name`: the ones physics and cameras filter by.
        private static IEnumerable<string> Layers()
        {
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                if (name.Length > 0)
                {
                    yield return i + " " + name;
                }
            }
        }

        private static string Pipeline()
        {
            RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
            return asset == null
                ? "render pipeline: Built-in"
                : "render pipeline: " + asset.GetType().Name + " (" + AssetDatabase.GetAssetPath(asset) + ")";
        }

        // The build list in order; enabled scenes carry the index Application.LoadLevel and SceneManager use.
        private static IEnumerable<string> BuildScenes(EditorBuildSettingsScene[] scenes)
        {
            if (scenes.Length == 0)
            {
                yield return "build scenes  none";
                yield break;
            }

            int enabled = scenes.Count(scene => scene.enabled);
            yield return "build scenes  " + enabled + " of " + scenes.Length + " enabled";
            int index = 0;
            foreach (EditorBuildSettingsScene scene in scenes)
            {
                yield return "  " + (scene.enabled ? (index++).ToString() : "-") + "  " + scene.path + (scene.enabled ? "" : "  (disabled)");
            }
        }
    }
}
