using System;
using System.Collections.Generic;
using System.Globalization;
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
        }

        /// <summary>The state to compare against; take it before the action starts.</summary>
        public static Digest Take(ConsoleBuffer console) => new Digest(console);

        /// <summary>The `  now:` line and the new errors under it. <paramref name="stopped"/>: play mode is ending.</summary>
        public List<string> Compare(bool stopped)
        {
            bool playing = EditorApplication.isPlaying && !stopped;
            var parts = new List<string>();
            var rows = new List<string>();
            string console = ConsolePart(rows);
            if (console.Length > 0)
            {
                parts.Add(console);
            }

            if (playing)
            {
                AddIfAny(parts, Scene());
                AddIfAny(parts, Objects());
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

        // `1 error, 3 warnings`, with up to two of the new errors as folded rows like `ug console`'s.
        private string ConsolePart(List<string> rows)
        {
            if (_console == null)
            {
                return "";
            }

            var errors = new Dictionary<(string Text, string Frame), (int Count, long Last)>();
            int errorCount = 0;
            int warningCount = 0;
            foreach (ConsoleBuffer.Entry entry in _console.Query(_consoleId, 500, null))
            {
                // A repeat of the entry that was last at the snapshot folds into it: count only the new ones.
                int count = entry.Count;
                if (_tail != null && entry.Count > _tail.Count && entry.Type == _tail.Type && entry.Text == _tail.Text && entry.Frame == _tail.Frame)
                {
                    count -= _tail.Count;
                }

                if (entry.Type == "warning")
                {
                    warningCount += count;
                }
                else if (entry.Type != "log")
                {
                    errorCount += count;
                    (string, string) key = (FirstLine(entry.Text), entry.Frame);
                    errors.TryGetValue(key, out (int Count, long Last) seen);
                    errors[key] = (seen.Count + count, Math.Max(seen.Last, entry.Id));
                }
            }

            foreach (KeyValuePair<(string Text, string Frame), (int Count, long Last)> error in errors.OrderByDescending(e => e.Value.Last).Take(ErrorRows))
            {
                rows.Add("  error" + (error.Value.Count > 1 ? " x" + error.Value.Count.ToString(CultureInfo.InvariantCulture) : "") + "  " + error.Key.Text + (error.Key.Frame == null ? "" : "  " + error.Key.Frame));
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

        // `changed Player (moved), Door (inactive), Bullet#3 (new), 2 gone`.
        private string Objects()
        {
            var now = new Dictionary<int, Item>();
            if (_over || !Scan(now))
            {
                return "objects not compared (over " + ObjectCap.ToString(CultureInfo.InvariantCulture) + ")";
            }

            var changes = new List<(int Id, GameObject Object, string What)>();
            int gone = 0;
            foreach (KeyValuePair<int, Item> before in _items)
            {
                if (!now.TryGetValue(before.Key, out Item after))
                {
                    gone++;
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
                parts.Add("+" + (changes.Count - NamedMax).ToString(CultureInfo.InvariantCulture) + " more");
            }

            if (gone > 0)
            {
                parts.Add(gone.ToString(CultureInfo.InvariantCulture) + " gone");
            }

            return parts.Count == 0 ? "" : "changed " + string.Join(", ", parts);
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

        private static string FirstLine(string text)
        {
            string line = (text ?? "").Replace("\r\n", "\n").Split('\n')[0].Trim();
            return line.Length > TextLimit ? line.Substring(0, TextLimit - 3) + "..." : line;
        }
    }
}
