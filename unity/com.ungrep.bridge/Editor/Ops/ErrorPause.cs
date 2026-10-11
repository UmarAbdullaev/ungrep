using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Ungrep.Bridge
{
    /// <summary>
    /// `ug play --pause-on-error` (SPEC-editor-ops, Play-mode debugging): armed, it pauses the editor at
    /// the first error, assert or exception logged in play mode, once, and keeps that entry for the
    /// answers until the editor resumes. Unity's Console Error Pause setting is never read or changed.
    /// </summary>
    internal sealed class ErrorPause
    {
        private static readonly string[] _errorType = { "error" };

        private readonly ConsoleBuffer _console;
        private readonly Func<bool> _playing;
        private readonly Action _pause;
        private volatile bool _armed;
        private long _deferred;
        private ConsoleBuffer.Entry _first;
        private int _frame;
        private float _at;

        public ErrorPause(ConsoleBuffer console, Func<bool> playing = null, Action pause = null)
        {
            _console = console;
            _playing = playing ?? (() => EditorApplication.isPlaying);
            _pause = pause ?? (() => EditorApplication.isPaused = true);
        }

        /// <summary>The bridge's error pause; set by the bridge, null when it is not running.</summary>
        internal static ErrorPause Current { get; set; }

        /// <summary>Whether the next error in play mode pauses; the bridge keeps it across domain reloads.</summary>
        public bool Armed
        {
            get => _armed;
            set => _armed = value;
        }

        public void Arm() => _armed = true;

        public void Disarm() => _armed = false;

        /// <summary>How many times it has paused: a digest compares it to see whether it fired meanwhile.</summary>
        public int Fires { get; private set; }

        /// <summary>Whether the editor is paused at an error it caught (until it resumes or play mode ends).</summary>
        public bool Fired => _first != null;

        /// <summary>
        /// An error entry was logged with this id. On the main thread it pauses at once, inside the log
        /// call, so no further frame runs; from another thread it waits for <see cref="Tick"/>.
        /// </summary>
        public void OnLog(long id, bool mainThread)
        {
            if (!_armed)
            {
                return;
            }

            if (mainThread)
            {
                Fire(id);
            }
            else
            {
                Interlocked.CompareExchange(ref _deferred, id, 0);
            }
        }

        /// <summary>On each editor update: an error logged from another thread pauses now.</summary>
        public void Tick()
        {
            long id = Interlocked.Exchange(ref _deferred, 0);
            if (id != 0 && _armed)
            {
                Fire(id);
            }
        }

        /// <summary>Leaving play mode drops the arm and the error kept.</summary>
        public void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode || change == PlayModeStateChange.EnteredEditMode)
            {
                _armed = false;
                _first = null;
                Interlocked.Exchange(ref _deferred, 0);
            }
        }

        /// <summary>Resuming forgets the error it paused at.</summary>
        public void OnPause(bool paused)
        {
            if (!paused)
            {
                _first = null;
            }
        }

        /// <summary>`paused at the first error, frame 214, 3.5 s in`, the error's row with its current id, and how to read it whole.</summary>
        public List<string> Lines()
        {
            ConsoleBuffer.Entry entry = Now();
            string id = entry.Id.ToString(CultureInfo.InvariantCulture);
            return new List<string>
            {
                "paused at the first error, frame " + _frame.ToString(CultureInfo.InvariantCulture) + ", " + _at.ToString("0.0", CultureInfo.InvariantCulture) + " s in",
                "  #" + id + " error" + (entry.Count > 1 ? " x" + entry.Count.ToString(CultureInfo.InvariantCulture) : "") + "  " + Digest.FirstLine(entry.Text)
                    + (entry.Frame == null ? "" : "  " + entry.Frame),
                "  more  whole: ug console --id " + id,
            };
        }

        /// <summary>The digest's play part: `paused at the first error #4105 (frame 214)`.</summary>
        public string PlayPart() =>
            "paused at the first error" + (_first == null ? "" : " #" + Now().Id.ToString(CultureInfo.InvariantCulture)) + " (frame " + _frame.ToString(CultureInfo.InvariantCulture) + ")";

        private void Fire(long id)
        {
            if (!_playing())
            {
                return;
            }

            _armed = false;
            _first = _console.Find(id) ?? new ConsoleBuffer.Entry { Id = id, Type = "error", Text = "", Count = 1 };
            _frame = Time.frameCount;
            _at = Time.unscaledTime;
            Fires++;
            _pause();
        }

        // The kept entry as the console holds it now: a repeat folded into it gave it a newer id.
        private ConsoleBuffer.Entry Now()
        {
            ConsoleBuffer.Entry first = _first;
            return _console.Find(first.Id)
                ?? _console.Query(first.Id, ConsoleBuffer.Capacity, _errorType).FirstOrDefault(e => e.Text == first.Text && e.Frame == first.Frame)
                ?? first;
        }
    }
}
