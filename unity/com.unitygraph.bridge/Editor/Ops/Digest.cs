using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// What an action changed (SPEC-play-input, Digest): a snapshot taken when it starts and a comparison
    /// after it settles, as the `  now:` line and up to two of the new errors. Component values are not
    /// compared; `ug inspect` reads them.
    /// </summary>
    internal sealed class Digest
    {
        public const int ObjectCap = 20000;
        public const string ChangesFile = "Library/unitygraph/digest.txt";
        private const int NamedMax = 3;
        private const int ErrorRows = 2;
        private const int TextLimit = 160;
        private const float Moved = 0.01f;

        private struct Item
        {
            public GameObject Object;
            public bool Active;
            public int Parent;
            public string Name;
            public Vector3 Position;
        }

        private readonly ConsoleBuffer _console;
        private readonly long _consoleId;
        private readonly ConsoleBuffer.Entry _tail;
        private readonly string _active;
        private readonly List<string> _scenes;
        private readonly Dictionary<int, Item> _items = new Dictionary<int, Item>();
        private readonly bool _over;
        private readonly bool _paused;
        private readonly int _fires;

        private Digest(ConsoleBuffer console)
        {
            _console = console;
            if (console != null)
            {
                _consoleId = console.LastId;
                _tail = console.Query(0, 1, null).LastOrDefault();
            }

            _scenes = LoadedScenes(out _active);
            _over = !Scan(_items);
            _paused = EditorApplication.isPaused;
            _fires = ErrorPause.Current?.Fires ?? 0;
        }

        /// <summary>The state to compare against; take it before the action starts.</summary>
        public static Digest Take(ConsoleBuffer console) => new Digest(console);

        /// <summary>The `  now:` line and the new errors under it. <paramref name="stopped"/>: play mode is ending.</summary>
        public List<string> Compare(bool stopped)
        {
            bool playing = EditorApplication.isPlaying && !stopped;
            var parts = new List<string>();
            var rows = new List<string>();
            string console = ConsolePart(_console, _consoleId, _tail, rows);
            if (console.Length > 0)
            {
                parts.Add(console);
            }

            if (playing)
            {
                AddIfAny(parts, Scene());
                AddIfAny(parts, Objects());
                AddIfAny(parts, Paused());
            }
            else
            {
                parts.Add("play stopped");
            }

            var lines = new List<string> { "  now: " + (parts.Count == 0 ? "nothing changed" : string.Join("; ", parts)) };
            lines.AddRange(rows);
            return lines;
        }

        private static void AddIfAny(List<string> parts, string part)
        {
            if (part.Length > 0)
            {
                parts.Add(part);
            }
        }

        // `paused at the first error #4105 (frame 214)` when the error pause fired meanwhile, `paused` when someone paused. Internal for the tests.
        internal string Paused()
        {
            ErrorPause errors = ErrorPause.Current;
            if (errors != null && errors.Fires != _fires)
            {
                return errors.PlayPart();
            }

            return EditorApplication.isPaused && !_paused ? "paused" : "";
        }

        /// <summary>
        /// `1 error, 3 warnings` logged after console id <paramref name="after"/>, with up to two of the
        /// errors as folded rows like `ug console`'s added to <paramref name="rows"/>. A repeat of
        /// <paramref name="tail"/>, the entry newest at <paramref name="after"/>, counts only its new repeats.
        /// </summary>
        internal static string ConsolePart(ConsoleBuffer console, long after, ConsoleBuffer.Entry tail, List<string> rows)
        {
            if (console == null)
            {
                return "";
            }

            var errors = new Dictionary<(string Text, string Frame), (int Count, long Last)>();
            var cutTexts = new HashSet<(string Text, string Frame)>();
            int errorCount = 0;
            int warningCount = 0;
            foreach (ConsoleBuffer.Entry entry in console.Query(after, 500, null))
            {
                // A repeat of the entry that was last at the snapshot folds into it: count only the new ones.
                int count = entry.Count;
                if (tail != null && entry.Count > tail.Count && entry.Type == tail.Type && entry.Text == tail.Text && entry.Frame == tail.Frame)
                {
                    count -= tail.Count;
                }

                if (entry.Type == "warning")
                {
                    warningCount += count;
                }
                else if (entry.Type != "log")
                {
                    errorCount += count;
                    (string, string) key = (FirstLine(entry.Text), entry.Frame);
                    if (key.Item1 != (entry.Text ?? "").Trim())
                    {
                        cutTexts.Add(key);
                    }

                    errors.TryGetValue(key, out (int Count, long Last) seen);
                    errors[key] = (seen.Count + count, Math.Max(seen.Last, entry.Id));
                }
            }

            bool cut = false;
            foreach (KeyValuePair<(string Text, string Frame), (int Count, long Last)> error in errors.OrderByDescending(e => e.Value.Last).Take(ErrorRows))
            {
                rows.Add("  error" + (error.Value.Count > 1 ? " x" + error.Value.Count.ToString(CultureInfo.InvariantCulture) : "") + "  " + error.Key.Text + (error.Key.Frame == null ? "" : "  " + error.Key.Frame));
                cut |= cutTexts.Contains(error.Key);
            }

            // A row cut at 160 characters or at its first line adds --whole to the line that names the console.
            string again = "ug console --after " + after.ToString(CultureInfo.InvariantCulture) + (cut ? " --whole" : "");
            if (errors.Count > ErrorRows)
            {
                rows.Add("  +" + (errors.Count - ErrorRows).ToString(CultureInfo.InvariantCulture) + " more errors: " + again);
            }
            else if (cut)
            {
                rows.Add("  (whole text: " + again + ")");
            }

            var parts = new List<string>();
            if (errorCount > 0)
            {
                parts.Add(Answer.Count(errorCount, "error", "errors"));
            }

            if (warningCount > 0)
            {
                parts.Add(Answer.Count(warningCount, "warning", "warnings"));
            }

            return string.Join(", ", parts);
        }

        // `scene Main -> Level1`, `scene +UI`, `scene -UI`.
        private string Scene()
        {
            List<string> now = LoadedScenes(out string active);
            var parts = new List<string>();
            if (active != _active)
            {
                parts.Add(_active + " -> " + active);
            }

            foreach (string name in now.Where(n => !_scenes.Contains(n) && n != active))
            {
                parts.Add("+" + name);
            }

            foreach (string name in _scenes.Where(n => !now.Contains(n) && n != _active))
            {
                parts.Add("-" + name);
            }

            return parts.Count == 0 ? "" : "scene " + string.Join(" ", parts);
        }

        // `changed Player (moved), Door (inactive), Bullet#3 (new), 2 gone`. Internal for the tests (Compare reads it only in play mode).
        internal string Objects()
        {
            var now = new Dictionary<int, Item>();
            if (_over || !Scan(now))
            {
                return "objects not compared (over " + ObjectCap.ToString(CultureInfo.InvariantCulture) + ", a bound on the snapshot's cost: ug find --live and ug tree read objects)";
            }

            var changes = new List<(int Id, GameObject Object, string What)>();
            var gone = new List<string>();
            foreach (KeyValuePair<int, Item> before in _items)
            {
                if (!now.TryGetValue(before.Key, out Item after))
                {
                    gone.Add(before.Value.Name);
                    continue;
                }

                var what = new List<string>();
                if ((after.Position - before.Value.Position).sqrMagnitude > Moved * Moved)
                {
                    what.Add("moved");
                }

                if (after.Active != before.Value.Active)
                {
                    what.Add(after.Active ? "active" : "inactive");
                }

                if (after.Name != before.Value.Name)
                {
                    what.Add("renamed");
                }

                if (after.Parent != before.Value.Parent)
                {
                    what.Add("reparented");
                }

                if (what.Count > 0)
                {
                    changes.Add((before.Key, after.Object, string.Join(", ", what)));
                }
            }

            changes.Sort((a, b) => a.Id.CompareTo(b.Id));
            var added = now.Where(i => !_items.ContainsKey(i.Key)).OrderBy(i => i.Key).Select(i => (Id: i.Key, Object: i.Value.Object, What: "new")).ToList();
            changes.AddRange(added);
            var parts = new List<string>();
            foreach ((int _, GameObject target, string what) in changes.Take(NamedMax))
            {
                parts.Add(Addresses.Segment(target) + " (" + what + ")");
            }

            if (changes.Count > NamedMax)
            {
                parts.Add("+" + (changes.Count - NamedMax).ToString(CultureInfo.InvariantCulture) + " more (" + WriteChanges(changes, gone) + ")");
            }

            if (gone.Count > 0)
            {
                parts.Add(gone.Count.ToString(CultureInfo.InvariantCulture) + " gone");
            }

            return parts.Count == 0 ? "" : "changed " + string.Join(", ", parts);
        }

        // Every change, one per line (`Level/Door (inactive)`, `Bullet (gone)`), in the changes file; its path, or why not.
        private static string WriteChanges(List<(int Id, GameObject Object, string What)> changes, List<string> gone)
        {
            try
            {
                string path = Path.Combine(Bridge.ProjectRoot, ChangesFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, string.Concat(changes.Select(c => Addresses.Of(c.Object) + " (" + c.What + ")\n").Concat(gone.Select(g => g + " (gone)\n"))));
                return ChangesFile;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "digest file not written: " + e.Message;
            }
        }

        private static List<string> LoadedScenes(out string active)
        {
            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                names.Add(SceneManager.GetSceneAt(i).name);
            }

            active = SceneManager.GetActiveScene().name;
            return names;
        }

        // The game objects of the loaded scenes; false above the cap.
        private static bool Scan(Dictionary<int, Item> into)
        {
            Transform[] all = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (all.Length > ObjectCap)
            {
                return false;
            }

            foreach (Transform t in all)
            {
                GameObject go = t.gameObject;
                if (!go.scene.IsValid() || (go.hideFlags & HideFlags.HideInHierarchy) != 0)
                {
                    continue;
                }

                into[go.GetInstanceID()] = new Item
                {
                    Object = go,
                    Active = go.activeSelf,
                    Parent = t.parent == null ? 0 : t.parent.gameObject.GetInstanceID(),
                    Name = go.name,
                    Position = t.position,
                };
            }

            return true;
        }

        internal static string FirstLine(string text)
        {
            string line = (text ?? "").Replace("\r\n", "\n").Split('\n')[0].Trim();
            return line.Length > TextLimit ? line.Substring(0, TextLimit - 3) + "..." : line;
        }
    }
}
