using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// One play-input action (SPEC-play-input): it starts, holds while the game runs, and releases.
    /// The queue runs it on editor updates, so the hold is the bridge's own timer and a client that
    /// goes away changes nothing.
    /// </summary>
    internal sealed class PlayAction
    {
        public PlayAction(string name)
        {
            Name = name;
        }

        /// <summary>How the action is called in refusals: `press Space`.</summary>
        public string Name { get; }

        /// <summary>Begins the action and answers its line; an error answer refuses it (nothing is held).</summary>
        public Func<Reply> Start { get; set; }

        /// <summary>
        /// Called each editor update with the real seconds and the frames since the start; true when the hold
        /// is over. Null: over at once.
        /// </summary>
        public Func<double, int, bool> Hold { get; set; }

        /// <summary>Puts back what Start or the moves changed: keys up, sticks centred, pointer up.</summary>
        public Action Release { get; set; }

        /// <summary>The answer; Start sets it, and a hold that learns more (a step count) may replace it.</summary>
        public Reply Result { get; set; }

        /// <summary>Whether the digest waits for the game to react (two frames and 0.15 s) after the release; a wait and a step do not.</summary>
        public bool Settle { get; set; } = true;

        /// <summary>
        /// A hold of <paramref name="seconds"/> real seconds that is never shorter than <paramref name="frames"/> frames
        /// (an input is read on a frame). <paramref name="move"/> gets 0 to 1 once per frame while it lasts.
        /// </summary>
        public static Func<double, int, bool> Lasting(double seconds, int frames, Action<float> move = null)
        {
            int last = -1;
            return (elapsed, passed) =>
            {
                if (elapsed >= seconds && passed >= frames)
                {
                    return true;
                }

                int frame = Time.frameCount;
                if (move != null && frame != last)
                {
                    last = frame;
                    move(Mathf.Clamp01((float)(elapsed / seconds)));
                }

                return false;
            };
        }
    }

    /// <summary>
    /// Runs one <see cref="PlayAction"/> at a time (SPEC-play-input, Safety 2 and 3): a second action is
    /// refused while one runs, and when play mode ends or the assemblies reload the action is released first.
    /// </summary>
    internal static class InputQueue
    {
        private sealed class Run
        {
            public PlayAction Action;
            public double Started;
            public int StartFrame;
            public Reply Result;
            public Digest Before;
            public bool Released;
            public bool Settling;
            public double SettledAt;
            public int SettledFrame;
        }

        // The digest waits this long after the last event: the game reacts on frames, and a scene may load.
        private const int SettleFrames = 2;
        private const double SettleSeconds = 0.15;

        private static Run _current;

        /// <summary>Starts the action and gives the poll that answers when it is over.</summary>
        public static Func<Reply> Start(PlayAction action)
        {
            if (_current != null)
            {
                Reply busy = Answer.Error("an input action is running (" + _current.Action.Name + "): wait for it, or ug stop");
                return () => busy;
            }

            var run = new Run { Action = action, Before = Digest.Take(PlayInput.Console), Started = EditorApplication.timeSinceStartup, StartFrame = Time.frameCount };
            _current = run;
            Reply started;
            try
            {
                started = action.Start();
            }
            catch
            {
                Drop(run);
                throw;
            }

            if (Answer.IsError(started))
            {
                _current = null;
                return () => started;
            }

            action.Result = started;
            return () => Poll(run);
        }

        /// <summary>The editor leaves play mode: release what is held.</summary>
        public static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                Abort("play mode ended");
                PlayInput.Devices?.Reset();
            }
        }

        /// <summary>Releases the running action and makes its request answer that it was cut short.</summary>
        public static void Abort(string why)
        {
            Run run = _current;
            if (run == null)
            {
                return;
            }

            if (run.Settling)
            {
                run.Result = Finish(run, true);
                return;
            }

            double elapsed = EditorApplication.timeSinceStartup - run.Started;
            Drop(run);
            run.Result = Answer.Line(run.Action.Name + ": " + why + " after " + Seconds(elapsed) + ", released");
        }

        public static string Seconds(double seconds) => seconds.ToString("0.0#", CultureInfo.InvariantCulture) + " s";

        private static Reply Poll(Run run)
        {
            if (run.Result != null)
            {
                return run.Result;
            }

            if (!EditorApplication.isPlaying)
            {
                if (run.Settling)
                {
                    return Finish(run, true);
                }

                Abort("play mode ended");
                return run.Result;
            }

            try
            {
                if (!run.Settling)
                {
                    double elapsed = EditorApplication.timeSinceStartup - run.Started;
                    if (run.Action.Hold != null && !run.Action.Hold(elapsed, Time.frameCount - run.StartFrame))
                    {
                        return null;
                    }

                    Release(run);
                    run.Settling = true;
                    run.SettledAt = EditorApplication.timeSinceStartup;
                    run.SettledFrame = Time.frameCount;
                }

                if (run.Action.Settle && !Settled(run))
                {
                    return null;
                }

                return Finish(run, false);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Drop(run);
                return Answer.Error("Unity threw " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Paused, no frame runs, so only the time counts.
        private static bool Settled(Run run) =>
            EditorApplication.timeSinceStartup - run.SettledAt >= SettleSeconds && (EditorApplication.isPaused || Time.frameCount - run.SettledFrame >= SettleFrames);

        // The action's answer and the digest of what it changed.
        private static Reply Finish(Run run, bool stopped)
        {
            if (_current == run)
            {
                _current = null;
            }

            return Answer.Extend(run.Action.Result, run.Before.Compare(stopped));
        }

        // Releases and forgets the run; a release that throws must not leave the queue stuck.
        private static void Drop(Run run)
        {
            if (_current == run)
            {
                _current = null;
            }

            Release(run);
        }

        private static void Release(Run run)
        {
            if (run.Released)
            {
                return;
            }

            run.Released = true;
            try
            {
                run.Action.Release?.Invoke();
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Debug.LogWarning("ug: releasing " + run.Action.Name + " failed: " + e.Message);
            }
        }
    }
}
