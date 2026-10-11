using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// Scenes and play mode (SPEC-editor-ops, Endpoints): list, open, new, close, set active, save,
    /// play, stop, pause. Answers are ug lines (<see cref="Answer"/>).
    /// </summary>
    internal static class SceneOps
    {
        private const string PlayStartKey = "unitygraph.play.start";
        private const string PlayConsoleKey = "unitygraph.play.console";
        private const double StartingSeconds = 10;

        // A paused or armed play start ug asked for, until play mode begins (editor seconds; 0: none).
        private static double _starting;
        private static bool _startingPaused;

        /// <summary>Null when the editor can take a request that reads or changes scenes, else the refusal.</summary>
        public static Reply Busy()
        {
            if (EditorApplication.isCompiling)
            {
                return Answer.Error("Unity is compiling; try again when ug status says idle");
            }

            return EditorApplication.isUpdating ? Answer.Error("Unity is importing assets; try again when ug status says idle") : null;
        }

        public static string PlayState() =>
            EditorApplication.isPlaying ? EditorApplication.isPaused ? "paused" : "playing" : "edit mode";

        public static Reply Scenes() => Answer.Lines(SceneLines());

        public static Reply Open(Request request)
        {
            string path = request.Get("path");
            bool additive = request.Get("additive") == "true";
            Reply refused = NotPlaying() ?? IsScene(path);
            if (refused != null)
            {
                return refused;
            }

            if (!additive)
            {
                refused = Unsaved();
                if (refused != null)
                {
                    return refused;
                }
            }
            else if (Addresses.LoadedScenes().Any(s => s.path == path))
            {
                return Answer.Error(path + " is already open");
            }

            EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return Answer.Lines(new[] { "opened " + path }.Concat(SceneLines()));
        }

        /// <summary>A new empty scene saved at `path`: alone (refused while a loaded scene is modified) or added.</summary>
        public static Reply New(Request request)
        {
            string path = request.Get("path");
            bool additive = request.Get("additive") == "true";
            Reply refused = NotPlaying();
            if (refused != null)
            {
                return refused;
            }

            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".unity", StringComparison.Ordinal))
            {
                return Answer.Error("a new scene needs a path like Assets/Scenes/Level2.unity");
            }

            if (File.Exists(Path.Combine(Bridge.ProjectRoot, path)))
            {
                return Answer.Error(path + " already exists: ug scene open " + path);
            }

            string folder = Path.GetDirectoryName(path).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
            {
                return Answer.Error("no folder " + folder + ": ug asset folder " + folder);
            }

            if (!additive)
            {
                refused = Unsaved();
                if (refused != null)
                {
                    return refused;
                }
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, additive ? NewSceneMode.Additive : NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, path))
            {
                return Answer.Error("Unity did not save " + path);
            }

            return Answer.Lines(new[] { "created " + path }.Concat(SceneLines()));
        }

        public static Reply Close(Request request)
        {
            string path = request.Get("path");
            Reply refused = NotPlaying();
            if (refused != null)
            {
                return refused;
            }

            Scene scene = Addresses.LoadedScenes().FirstOrDefault(s => s.path == path || s.name == path);
            if (!scene.IsValid())
            {
                return Answer.Error(path + " is not open");
            }

            if (SceneManager.sceneCount == 1)
            {
                return Answer.Error(scene.path + " is the only open scene: ug scene open another one instead");
            }

            if (scene.isDirty)
            {
                return Answer.Error("unsaved changes in " + Name(scene) + ": ug save first");
            }

            EditorSceneManager.CloseScene(scene, true);
            return Answer.Lines(new[] { "closed " + path }.Concat(SceneLines()));
        }

        public static Reply Activate(Request request)
        {
            string path = request.Get("path");
            Scene scene = Addresses.LoadedScenes().FirstOrDefault(s => s.path == path || s.name == path);
            if (!scene.IsValid())
            {
                return Answer.Error(path + " is not open");
            }

            SceneManager.SetActiveScene(scene);
            return Answer.Lines(SceneLines());
        }

        /// <summary>Saves the modified loaded scenes (or the one at `path`) and dirty assets: File > Save.</summary>
        public static Reply Save(Request request)
        {
            if (EditorApplication.isPlaying)
            {
                return Answer.Error("Unity is in play mode: scenes cannot be saved until ug stop");
            }

            string path = request.Get("path");
            List<Scene> scenes = Addresses.LoadedScenes().Where(s => string.IsNullOrEmpty(path) ? s.isDirty : s.path == path || s.name == path).ToList();
            if (!string.IsNullOrEmpty(path) && scenes.Count == 0)
            {
                return Answer.Error(path + " is not open");
            }

            if (scenes.Any(s => string.IsNullOrEmpty(s.path)))
            {
                return Answer.Error("an untitled scene has changes: save it once in Unity to give it a path");
            }

            var lines = new List<string>();
            foreach (Scene scene in scenes)
            {
                if (!EditorSceneManager.SaveScene(scene))
                {
                    return Answer.Error("Unity did not save " + scene.path);
                }

                lines.Add("saved " + scene.path);
            }

            AssetDatabase.SaveAssets();
            if (lines.Count == 0)
            {
                lines.Add("no modified scenes; assets saved");
            }

            return Answer.Lines(lines);
        }

        /// <summary>
        /// Enters play mode, or resumes a pause. `paused=true` starts on the first frame (or keeps a pause);
        /// `onerror=true` arms the error pause (SPEC-editor-ops, Play-mode debugging).
        /// </summary>
        public static Reply Play(Request request)
        {
            bool paused = request.Get("paused") == "true";
            bool onError = request.Get("onerror") == "true";
            if (EditorApplication.isPlaying)
            {
                if (!EditorApplication.isPaused && paused)
                {
                    return Answer.Error("already playing: ug pause pauses here; ug stop first to start again on frame 1");
                }

                if (!EditorApplication.isPaused && !onError)
                {
                    return Answer.Error("already playing");
                }

                if (onError)
                {
                    ErrorPause.Current?.Arm();
                }

                if (!paused)
                {
                    EditorApplication.isPaused = false;
                }

                return Answer.Line(EditorApplication.isPaused ? "paused" : "playing");
            }

            if (EditorUtility.scriptCompilationFailed)
            {
                return Answer.Error("scripts have compile errors: ug compile lists them");
            }

            if (onError)
            {
                ErrorPause.Current?.Arm();
            }

            if (paused)
            {
                EditorApplication.isPaused = true;
            }

            if (paused || onError)
            {
                _starting = EditorApplication.timeSinceStartup;
                _startingPaused = paused;
            }

            EditorApplication.EnterPlaymode();
            return Answer.Lines(new[] { "playing" }, wait: "playing");
        }

        /// <summary>
        /// `/play/state`: where play mode is (paused at the first error, paused or playing on a frame) and
        /// what loaded, once the first frame has run (at most 5 s).
        /// </summary>
        public static Func<Reply> State(ConsoleBuffer console)
        {
            if (!EditorApplication.isPlaying)
            {
                Reply refused = Answer.Error("not playing: ug play first");
                return () => refused;
            }

            double asked = EditorApplication.timeSinceStartup;
            return () =>
            {
                if (!EditorApplication.isPlaying)
                {
                    return Answer.Error("not playing: ug play first");
                }

                return Time.frameCount < 1 && EditorApplication.timeSinceStartup - asked < 5 ? null : Answer.Lines(StateLines(console));
            };
        }

        /// <summary>On each editor update: a play start `ug play` asked for that never came takes back its pause and its arm.</summary>
        public static void Tick()
        {
            if (_starting == 0 || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup - _starting < StartingSeconds)
            {
                return;
            }

            _starting = 0;
            if (_startingPaused)
            {
                EditorApplication.isPaused = false;
            }

            ErrorPause.Current?.Disarm();
        }

        public static Reply Pause()
        {
            if (!EditorApplication.isPlaying)
            {
                return Answer.Error("not playing");
            }

            EditorApplication.isPaused = true;
            return Answer.Line("paused");
        }

        /// <summary>Leaves play mode; answers how long it played and the errors and warnings logged meanwhile.</summary>
        public static Reply Stop(ConsoleBuffer console)
        {
            if (!EditorApplication.isPlaying)
            {
                return Answer.Error("not playing");
            }

            string line = "stopped";
            if (long.TryParse(SessionState.GetString(PlayStartKey, ""), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            {
                double seconds = (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds;
                line += " after " + seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            }

            long after = long.TryParse(SessionState.GetString(PlayConsoleKey, ""), NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : 0;
            List<ConsoleBuffer.Entry> entries = console.Query(after, ConsoleBuffer.Capacity, new[] { "error", "warning" });
            int errors = entries.Where(e => e.Type == "error").Sum(e => e.Count);
            int warnings = entries.Where(e => e.Type == "warning").Sum(e => e.Count);
            line += ": " + Answer.Count(errors, "error", "errors") + ", " + Answer.Count(warnings, "warning", "warnings");
            if (errors + warnings > 0)
            {
                line += " (ug console)";
            }

            EditorApplication.ExitPlaymode();
            return Answer.Lines(new[] { line }, wait: "idle");
        }

        /// <summary>Remembers when play started, from `ug play` or the user, across the domain reload that follows.</summary>
        public static void OnPlayModeChanged(PlayModeStateChange change, long consoleId)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                _starting = 0;
                SessionState.SetString(PlayStartKey, DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture));
                SessionState.SetString(PlayConsoleKey, consoleId.ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// `Boot (active, 3 roots, 41 objects), DontDestroyOnLoad (2 roots, 9 objects)`: the loaded scenes in
        /// hierarchy order, then any other scene holding objects; objects hidden in the hierarchy left out.
        /// </summary>
        internal static string Loaded()
        {
            var counts = new Dictionary<int, int[]>();
            var order = new List<Scene>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!counts.ContainsKey(scene.handle))
                {
                    counts[scene.handle] = new int[2];
                    order.Add(scene);
                }
            }

            foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Scene scene = t.gameObject.scene;
                if (!scene.IsValid() || (t.gameObject.hideFlags & HideFlags.HideInHierarchy) != 0)
                {
                    continue;
                }

                if (!counts.TryGetValue(scene.handle, out int[] count))
                {
                    counts[scene.handle] = count = new int[2];
                    order.Add(scene);
                }

                count[1]++;
                if (t.parent == null)
                {
                    count[0]++;
                }
            }

            Scene active = SceneManager.GetActiveScene();
            return string.Join(", ", order.Select(s => (string.IsNullOrEmpty(s.name) ? "untitled scene" : s.name) + " (" + (s == active ? "active, " : "")
                + Answer.Count(counts[s.handle][0], "root", "roots") + ", " + Answer.Count(counts[s.handle][1], "object", "objects") + ")"));
        }

        // The state lines: the error the pause caught, else the play state, what loaded and what play mode logged so far.
        private static List<string> StateLines(ConsoleBuffer console)
        {
            ErrorPause errors = ErrorPause.Current;
            if (errors != null && errors.Fired && EditorApplication.isPaused)
            {
                return errors.Lines();
            }

            string state = (EditorApplication.isPaused ? "paused" : "playing") + " on frame " + Time.frameCount.ToString(CultureInfo.InvariantCulture)
                + (errors != null && errors.Armed ? "; pauses at the first error" : "");
            long after = long.TryParse(SessionState.GetString(PlayConsoleKey, ""), NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : 0;
            var rows = new List<string>();
            string logged = Digest.ConsolePart(console, after, null, rows);
            var lines = new List<string> { state, "  loaded: " + Loaded() + (logged.Length > 0 ? "; " + logged : "") };
            lines.AddRange(rows);
            return lines;
        }

        /// <summary>`Assets/Scenes/Main.unity  active  modified  12 roots` per loaded scene, then the play state.</summary>
        public static List<string> SceneLines()
        {
            var lines = new List<string>();
            Scene active = SceneManager.GetActiveScene();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                string line = Name(scene);
                if (scene == active)
                {
                    line += "  active";
                }

                if (scene.isDirty)
                {
                    line += "  modified";
                }

                line += scene.isLoaded ? "  " + Answer.Count(scene.rootCount, "root", "roots") : "  not loaded";
                lines.Add(line);
            }

            lines.Add(PlayState());
            return lines;
        }

        private static Reply NotPlaying() =>
            EditorApplication.isPlayingOrWillChangePlaymode ? Answer.Error("Unity is in play mode: ug stop first") : null;

        private static Reply IsScene(string path) =>
            string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null ? Answer.Error("not a scene: " + path) : null;

        private static Reply Unsaved()
        {
            List<string> modified = Addresses.LoadedScenes().Where(s => s.isDirty).Select(Name).ToList();
            return modified.Count > 0 ? Answer.Error("unsaved changes in " + string.Join(", ", modified) + ": ug save, or --additive to keep them open") : null;
        }

        internal static string Name(Scene scene) => string.IsNullOrEmpty(scene.path) ? "untitled scene" : scene.path;
    }
}
