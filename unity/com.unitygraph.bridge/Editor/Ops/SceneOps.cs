using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Scenes and play mode (SPEC-editor-ops, Endpoints): list, open, save, play, stop, pause.
    /// Every answer is 200: the result, or `{"error": ...}` for a refusal the CLI prints as is.
    /// </summary>
    internal static class SceneOps
    {
        private const string PlayStartKey = "unitygraph.play.start";
        private const string PlayConsoleKey = "unitygraph.play.console";

        public static Reply Error(string message) => Reply.Ok(new Json().Object().Field("error", message).EndObject().ToString());

        /// <summary>Null when the editor can take a request that reads or changes scenes, else the refusal.</summary>
        public static Reply Busy()
        {
            if (EditorApplication.isCompiling)
            {
                return Error("Unity is compiling; try again when ug status says idle");
            }

            return EditorApplication.isUpdating ? Error("Unity is importing assets; try again when ug status says idle") : null;
        }

        public static string PlayState() =>
            EditorApplication.isPlaying ? EditorApplication.isPaused ? "paused" : "playing" : "edit";

        public static Reply Scenes() => Reply.Ok(WriteScenes(new Json().Object()).EndObject().ToString());

        public static Reply Open(Request request)
        {
            string path = request.Get("path");
            bool additive = request.Get("additive") == "true";
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return Error("Unity is in play mode: ug stop first");
            }

            if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
            {
                return Error("not a scene: " + path);
            }

            if (!additive)
            {
                List<string> modified = Addresses.LoadedScenes().Where(s => s.isDirty).Select(Name).ToList();
                if (modified.Count > 0)
                {
                    return Error("unsaved changes in " + string.Join(", ", modified) + ": ug save, or --additive to keep them open");
                }
            }
            else if (Addresses.LoadedScenes().Any(s => s.path == path))
            {
                return Error(path + " is already open");
            }

            EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return Reply.Ok(WriteScenes(new Json().Object().Field("opened", path)).EndObject().ToString());
        }

        /// <summary>Saves the modified loaded scenes (or the one at `path`) and dirty assets: File > Save.</summary>
        public static Reply Save(Request request)
        {
            if (EditorApplication.isPlaying)
            {
                return Error("Unity is in play mode: scenes cannot be saved until ug stop");
            }

            string path = request.Get("path");
            List<Scene> scenes = Addresses.LoadedScenes().Where(s => string.IsNullOrEmpty(path) ? s.isDirty : s.path == path).ToList();
            if (!string.IsNullOrEmpty(path) && scenes.Count == 0)
            {
                return Error(path + " is not open");
            }

            Scene untitled = scenes.FirstOrDefault(s => string.IsNullOrEmpty(s.path));
            if (untitled.IsValid())
            {
                return Error("an untitled scene has changes: save it once in Unity to give it a path");
            }

            var json = new Json().Object().Key("saved").Array();
            foreach (Scene scene in scenes)
            {
                if (!EditorSceneManager.SaveScene(scene))
                {
                    return Error("Unity did not save " + scene.path);
                }

                json.Value(scene.path);
            }

            AssetDatabase.SaveAssets();
            return Reply.Ok(json.EndArray().EndObject().ToString());
        }

        public static Reply Play()
        {
            if (EditorApplication.isPlaying)
            {
                if (!EditorApplication.isPaused)
                {
                    return Error("already playing");
                }

                EditorApplication.isPaused = false;
                return Reply.Ok(new Json().Object().Field("state", "playing").EndObject().ToString());
            }

            if (EditorUtility.scriptCompilationFailed)
            {
                return Error("scripts have compile errors: ug compile lists them");
            }

            EditorApplication.EnterPlaymode();
            return Reply.Ok(new Json().Object().Field("state", "entering").EndObject().ToString());
        }

        public static Reply Pause()
        {
            if (!EditorApplication.isPlaying)
            {
                return Error("not playing");
            }

            EditorApplication.isPaused = true;
            return Reply.Ok(new Json().Object().Field("state", "paused").EndObject().ToString());
        }

        /// <summary>Leaves play mode; answers how long it played and the errors and warnings logged meanwhile.</summary>
        public static Reply Stop(ConsoleBuffer console)
        {
            if (!EditorApplication.isPlaying)
            {
                return Error("not playing");
            }

            var json = new Json().Object().Field("state", "stopping");
            if (long.TryParse(SessionState.GetString(PlayStartKey, ""), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            {
                json.Field("seconds", Math.Round((DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds, 1));
            }

            long after = long.TryParse(SessionState.GetString(PlayConsoleKey, ""), NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : 0;
            List<ConsoleBuffer.Entry> entries = console.Query(after, ConsoleBuffer.Capacity, new[] { "error", "warning" });
            json.Field("errors", entries.Where(e => e.Type == "error").Sum(e => (long)e.Count))
                .Field("warnings", entries.Where(e => e.Type == "warning").Sum(e => (long)e.Count))
                .Field("console", after);
            EditorApplication.ExitPlaymode();
            return Reply.Ok(json.EndObject().ToString());
        }

        /// <summary>Remembers when play started, from `ug play` or the user, across the domain reload that follows.</summary>
        public static void OnPlayModeChanged(PlayModeStateChange change, long consoleId)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                SessionState.SetString(PlayStartKey, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                SessionState.SetString(PlayConsoleKey, consoleId.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static Json WriteScenes(Json json)
        {
            json.Field("play", PlayState()).Key("scenes").Array();
            Scene active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                json.Object()
                    .Field("path", scene.path)
                    .Field("name", scene.name)
                    .Field("active", scene == active)
                    .Field("modified", scene.isDirty)
                    .Field("loaded", scene.isLoaded)
                    .Field("roots", scene.isLoaded ? scene.rootCount : 0)
                    .EndObject();
            }

            return json.EndArray();
        }

        private static string Name(Scene scene) => string.IsNullOrEmpty(scene.path) ? "an untitled scene" : scene.path;
    }
}
