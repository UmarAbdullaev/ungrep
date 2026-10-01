using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// The last play session for `ug log` (specs/SPEC-unity-loop.md, Bridge change): when play mode
    /// started and ended, the console ids around it and the pauses. Kept in SessionState so it
    /// survives the domain reloads that entering play mode causes; lost when the editor quits.
    /// </summary>
    internal sealed class PlayLog
    {
        private const string Key = "unitygraph.bridge.playlog";
        private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        private readonly List<KeyValuePair<double, string>> _events = new List<KeyValuePair<double, string>>();
        private long _start;
        private long _end;
        private long _after;
        private long _until;

        internal bool HasSession => _start != 0;

        internal bool Playing => _start != 0 && _end == 0;

        /// <summary>A session starts when the editor leaves edit mode and ends when it leaves play mode.</summary>
        internal void OnPlayModeChanged(PlayModeStateChange change, DateTime now, long consoleId)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
            {
                _start = now.Ticks;
                _after = consoleId;
                _end = 0;
                _until = 0;
                _events.Clear();
            }
            else if (change == PlayModeStateChange.ExitingPlayMode && Playing)
            {
                _end = now.Ticks;
                _until = consoleId;
            }
        }

        internal void OnPause(bool paused, DateTime now)
        {
            if (Playing)
            {
                _events.Add(new KeyValuePair<double, string>(Math.Round(new TimeSpan(now.Ticks - _start).TotalSeconds, 1), paused ? "pause" : "resume"));
            }
        }

        internal string Answer()
        {
            if (!HasSession)
            {
                return "{\"session\":null}";
            }

            var json = new Json().Object().Key("session").Object()
                .Field("start", Time(_start))
                .Field("end", _end == 0 ? null : Time(_end))
                .Field("after", _after);
            json = _end == 0 ? json.Key("until").Raw("null") : json.Field("until", _until);
            json.Key("events").Array();
            foreach (KeyValuePair<double, string> e in _events)
            {
                json.Object().Field("at", e.Key).Field("what", e.Value).EndObject();
            }

            return json.EndArray().EndObject().EndObject().ToString();
        }

        internal string Save()
        {
            var parts = new List<string>
            {
                _start.ToString(CultureInfo.InvariantCulture),
                _end.ToString(CultureInfo.InvariantCulture),
                _after.ToString(CultureInfo.InvariantCulture),
                _until.ToString(CultureInfo.InvariantCulture),
            };
            foreach (KeyValuePair<double, string> e in _events)
            {
                parts.Add(e.Key.ToString("R", CultureInfo.InvariantCulture) + ":" + e.Value);
            }

            return string.Join(";", parts);
        }

        internal void Restore(string saved)
        {
            _events.Clear();
            _start = _end = _after = _until = 0;
            string[] parts = saved.Split(';');
            if (parts.Length < 4
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long start)
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long end)
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long after)
                || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long until))
            {
                return;
            }

            _start = start;
            _end = end;
            _after = after;
            _until = until;
            for (int i = 4; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon > 0 && double.TryParse(parts[i].Substring(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out double at))
                {
                    _events.Add(new KeyValuePair<double, string>(at, parts[i].Substring(colon + 1)));
                }
            }
        }

        internal void Persist() => SessionState.SetString(Key, Save());

        internal void Load() => Restore(SessionState.GetString(Key, ""));

        private static string Time(long ticks) => new DateTime(ticks, DateTimeKind.Utc).ToString(TimeFormat, CultureInfo.InvariantCulture);
    }
}
