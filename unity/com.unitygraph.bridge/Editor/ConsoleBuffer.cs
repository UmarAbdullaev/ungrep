using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// The console entries seen since the editor started, newest last, at most
    /// <see cref="Capacity"/>, each with its whole message and stack up to <see cref="MaxText"/>
    /// characters (memory bounds, SPEC-unity-loop, Nothing out of reach). A repeat of the newest
    /// entry bumps its count and gives it a new id, so a reader polling `after` sees it again.
    /// Thread-safe: Unity logs from any thread.
    /// </summary>
    internal sealed class ConsoleBuffer
    {
        public const int Capacity = 2000;
        public const int MaxText = 8000;

        private readonly object _lock = new object();
        private readonly LinkedList<Entry> _entries = new LinkedList<Entry>();
        private long _lastId;

        internal sealed class Entry
        {
            public long Id;
            public string Type;
            public string Text;
            public string Frame;
            public string Stack;
            public int Count;
        }

        public long LastId
        {
            get
            {
                lock (_lock)
                {
                    return _lastId;
                }
            }
        }

        public static string TypeName(LogType type)
        {
            switch (type)
            {
                case LogType.Warning: return "warning";
                case LogType.Log: return "log";
                default: return "error";
            }
        }

        /// <summary>The id of the oldest entry held, 0 when none.</summary>
        public long OldestId
        {
            get
            {
                lock (_lock)
                {
                    return _entries.First?.Value.Id ?? 0;
                }
            }
        }

        /// <summary><paramref name="text"/> cut to <see cref="MaxText"/>, saying so.</summary>
        public static string Bound(string text) =>
            text.Length > MaxText ? text.Substring(0, MaxText) + "... (cut at " + MaxText.ToString(CultureInfo.InvariantCulture) + " characters)" : text;

        public void Add(string type, string text, string frame, string stack = null)
        {
            text = Bound(text ?? "");
            stack = string.IsNullOrEmpty(stack) ? null : Bound(stack.TrimEnd());
            lock (_lock)
            {
                Entry last = _entries.Last?.Value;
                if (last != null && last.Type == type && last.Text == text && last.Frame == frame)
                {
                    last.Count++;
                    last.Id = ++_lastId;
                    return;
                }

                _entries.AddLast(new Entry { Id = ++_lastId, Type = type, Text = text, Frame = frame, Stack = stack, Count = 1 });
                if (_entries.Count > Capacity)
                {
                    _entries.RemoveFirst();
                }
            }
        }

        /// <summary>
        /// Entries newer than <paramref name="after"/> and older than <paramref name="before"/> (0: no
        /// bound) of the given types, oldest first, at most <paramref name="limit"/> (the newest ones).
        /// </summary>
        public List<Entry> Query(long after, int limit, ICollection<string> types, long before = 0)
        {
            var found = new List<Entry>();
            lock (_lock)
            {
                for (LinkedListNode<Entry> node = _entries.Last; node != null && node.Value.Id > after && found.Count < limit; node = node.Previous)
                {
                    if ((before <= 0 || node.Value.Id < before) && (types == null || types.Contains(node.Value.Type)))
                    {
                        found.Add(Copy(node.Value));
                    }
                }
            }

            found.Sort((a, b) => a.Id.CompareTo(b.Id));
            return found;
        }

        /// <summary>The entry with this id, or null when the buffer no longer holds it.</summary>
        public Entry Find(long id)
        {
            lock (_lock)
            {
                for (LinkedListNode<Entry> node = _entries.Last; node != null && node.Value.Id >= id; node = node.Previous)
                {
                    if (node.Value.Id == id)
                    {
                        return Copy(node.Value);
                    }
                }
            }

            return null;
        }

        public static void Write(Json json, Entry entry)
        {
            json.Object()
                .Field("id", entry.Id)
                .Field("type", entry.Type)
                .Field("text", entry.Text)
                .Field("frame", entry.Frame)
                .Field("stack", entry.Stack)
                .Field("count", entry.Count)
                .EndObject();
        }

        /// <summary>One line per entry, fields tab-separated and escaped, for SessionState across a domain reload.</summary>
        public string Save()
        {
            var text = new StringBuilder();
            lock (_lock)
            {
                text.Append(_lastId.ToString(CultureInfo.InvariantCulture)).Append('\n');
                foreach (Entry entry in _entries)
                {
                    text.Append(entry.Id.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(Encode(entry.Type)).Append('\t')
                        .Append(Encode(entry.Text)).Append('\t')
                        .Append(entry.Frame == null ? "-" : "+" + Encode(entry.Frame)).Append('\t')
                        .Append(entry.Count.ToString(CultureInfo.InvariantCulture)).Append('\t')
                        .Append(entry.Stack == null ? "-" : "+" + Encode(entry.Stack)).Append('\n');
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// Restores what <see cref="Save"/> wrote; unreadable text leaves the buffer empty. A buffer
        /// saved by a bridge before 0.9.0 has five fields and restores with no stacks.
        /// </summary>
        public void Restore(string saved)
        {
            if (string.IsNullOrEmpty(saved))
            {
                return;
            }

            var entries = new List<Entry>();
            string[] lines = saved.Split('\n');
            if (!long.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out long lastId))
            {
                return;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                string[] fields = lines[i].Split('\t');
                if ((fields.Length != 5 && fields.Length != 6)
                    || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out long id)
                    || !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int count))
                {
                    continue;
                }

                string frame = fields[3].StartsWith("+", StringComparison.Ordinal) ? Decode(fields[3].Substring(1)) : null;
                string stack = fields.Length == 6 && fields[5].StartsWith("+", StringComparison.Ordinal) ? Decode(fields[5].Substring(1)) : null;
                entries.Add(new Entry { Id = id, Type = Decode(fields[1]), Text = Decode(fields[2]), Frame = frame, Stack = stack, Count = count });
            }

            lock (_lock)
            {
                _entries.Clear();
                foreach (Entry entry in entries)
                {
                    _entries.AddLast(entry);
                }

                _lastId = Math.Max(_lastId, lastId);
            }
        }

        private static Entry Copy(Entry e) => new Entry { Id = e.Id, Type = e.Type, Text = e.Text, Frame = e.Frame, Stack = e.Stack, Count = e.Count };

        private static string Encode(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

        private static string Decode(string value)
        {
            var text = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    char next = value[++i];
                    text.Append(next == 't' ? '\t' : next == 'n' ? '\n' : next == 'r' ? '\r' : next);
                }
                else
                {
                    text.Append(value[i]);
                }
            }

            return text.ToString();
        }
    }
}
