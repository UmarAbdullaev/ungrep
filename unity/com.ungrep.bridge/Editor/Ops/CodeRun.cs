using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Ungrep.Bridge
{
    /// <summary>
    /// Runs code for `ug run` and `ug eval` (SPEC-editor-link, Running code): the call in one undo
    /// group, then the answer both share: the result through <see cref="Values.Show"/>, the
    /// exception with its first own frame, what was logged and which scenes it left modified. A
    /// returned awaitable or IEnumerator is waited for across editor updates; past the deadline the
    /// answer says so and the code goes on stepping in the editor.
    /// </summary>
    internal static class CodeRun
    {
        public const int MaxLogs = 5;
        public const double DefaultTimeout = 60;
        public const string ResultFile = "Library/ungrep/result.txt";
        public const string ExceptionFile = "Library/ungrep/exception.txt";
        private const int MaxMessage = 300;
        private const int MaxLogText = 160;

        private static readonly List<Pending> _running = new List<Pending>();
        private static readonly FieldInfo _waitSeconds = typeof(WaitForSeconds).GetField("m_Seconds", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>A call's answer that is a refusal, not a run (a menu path that names no item).</summary>
        internal sealed class Refusal
        {
            public string Message;
        }

        /// <summary>One call: what to print as its name, how to call it, and its `out` values after.</summary>
        internal sealed class Call
        {
            public string What;
            public Func<object> Invoke;
            public bool HasResult;
            public Func<IEnumerable<KeyValuePair<string, object>>> Outs;
            public double Timeout = DefaultTimeout;
            public string Snippet;
        }

        private sealed class Pending
        {
            public Call Call;
            public Stopwatch Clock;
            public ConsoleBuffer Console;
            public long ConsoleId;
            public ConsoleBuffer.Entry Tail;
            public HashSet<int> DirtyBefore;
            public Wait Wait;
            public object Result;
            public Exception Error;
        }

        /// <summary>Calls now; the poll answers when the call and what it returned are done, or at the deadline.</summary>
        public static Func<Reply> Start(Call call, ConsoleBuffer console)
        {
            var run = new Pending
            {
                Call = call,
                Console = console,
                ConsoleId = console?.LastId ?? 0,
                Tail = console?.Query(0, 1, null).LastOrDefault(),
                DirtyBefore = DirtyScenes(),
                Clock = Stopwatch.StartNew(),
            };

            if (call.Snippet != null)
            {
                StackFrames.AddEvalSource(call.Snippet);
            }

            // One group, not named: Undo.SetCurrentGroupName adds a record even to an empty group, so a
            // call that records nothing would put its name over the last change's in `ug undo`.
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            try
            {
                run.Result = Values.Materialize(call.Invoke());
                run.Wait = Wait.For(run.Result);
                if (run.Wait != null && !run.Wait.Step())
                {
                    _running.Add(run);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                run.Error = Unwrap(e);
            }
            finally
            {
                Undo.CollapseUndoOperations(group);
            }

            if (run.Result is Refusal refusal)
            {
                Reply refused = Answer.Error(refusal.Message);
                return () => refused;
            }

            return () =>
            {
                if (!_running.Contains(run))
                {
                    return Finish(run);
                }

                return run.Clock.Elapsed.TotalSeconds < call.Timeout ? null : StillRunning(run);
            };
        }

        /// <summary>Steps every running wait once; Bridge calls it on each editor update before answering requests.</summary>
        public static void Tick()
        {
            for (int i = _running.Count - 1; i >= 0; i--)
            {
                Pending run = _running[i];
                bool done;
                try
                {
                    done = run.Wait.Step();
                }
                catch (Exception e) when (!(e is OutOfMemoryException))
                {
                    run.Wait.Error = Unwrap(e);
                    done = true;
                }

                if (done)
                {
                    _running.RemoveAt(i);
                }
            }
        }

        /// <summary>`12 ms`, `0.4 ms`.</summary>
        public static string Ms(double milliseconds) =>
            milliseconds.ToString(milliseconds < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);

        public static Exception Unwrap(Exception e)
        {
            while (true)
            {
                if (e is TargetInvocationException && e.InnerException != null)
                {
                    e = e.InnerException;
                }
                else if (e is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                {
                    e = aggregate.InnerExceptions[0];
                }
                else
                {
                    return e;
                }
            }
        }

        private static Reply Finish(Pending run)
        {
            Exception error = run.Error ?? run.Wait?.Error;
            string ms = Ms(run.Clock.Elapsed.TotalMilliseconds);
            var lines = new List<string>();
            if (error != null)
            {
                lines.Add(run.Call.What + " threw after " + ms + " ms: " + error.GetType().Name + ": " + Message(error));
                string frame = StackFrames.OwnFrame(error.StackTrace, Bridge.ProjectRoot);
                if (frame != null)
                {
                    lines.Add("  at " + frame);
                }

                lines.Add(WriteException(error));
            }
            else
            {
                lines.Add("ran " + run.Call.What + " in " + ms + " ms");
                var shown = new List<KeyValuePair<string, object>>();
                if (run.Wait is Stepped steps)
                {
                    lines.Add("  result: finished after " + Answer.Count(steps.Steps, "step", "steps"));
                }
                else if (run.Wait != null ? run.Wait.HasResult : run.Call.HasResult)
                {
                    shown.Add(new KeyValuePair<string, object>("result", run.Wait != null ? run.Wait.Result : run.Result));
                }

                if (run.Call.Outs != null)
                {
                    shown.AddRange(run.Call.Outs().Select(o => new KeyValuePair<string, object>("out " + o.Key, o.Value)));
                }

                lines.AddRange(Shown(shown));
            }

            lines.AddRange(Logs(run));
            lines.AddRange(Changed(run));

            // A throw is a failed step: `ug batch` stops on an error answer.
            return error != null ? Answer.Error(string.Join("\n", lines)) : Answer.Lines(lines);
        }

        // The whole exception, every inner one with its stack, to the exception file; the line that names it.
        private static string WriteException(Exception error)
        {
            try
            {
                string path = Path.Combine(Bridge.ProjectRoot, ExceptionFile);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, error + "\n");
                return "  whole exception: " + ExceptionFile;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "  (exception file not written: " + e.Message + ")";
            }
        }

        private static Reply StillRunning(Pending run)
        {
            var lines = new List<string>
            {
                run.Call.What + " still running after " + run.Call.Timeout.ToString("0.#", CultureInfo.InvariantCulture) + " s; it goes on in the editor",
            };
            lines.AddRange(Logs(run));
            return Answer.Lines(lines);
        }

        // `  result: value`, each cut at the compact caps; when anything was cut, all of them whole go to the result file.
        private static List<string> Shown(List<KeyValuePair<string, object>> values)
        {
            var lines = new List<string>();
            bool anyCut = false;
            foreach (KeyValuePair<string, object> value in values)
            {
                string text = Values.Show(value.Value, Values.Caps.Compact, out bool cut);
                lines.Add("  " + value.Key + ": " + text + (cut ? " (+more: " + ResultFile + ")" : ""));
                anyCut |= cut;
            }

            if (anyCut)
            {
                try
                {
                    string path = Path.Combine(Bridge.ProjectRoot, ResultFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, string.Concat(values.Select(v => v.Key + ": " + Values.Show(v.Value, Values.Caps.Full, out _) + "\n")));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    lines.Add("  (result file not written: " + e.Message + ")");
                }
            }

            return lines;
        }

        // What the call logged, oldest first, at most MaxLogs; a repeat of the last entry before it counts only its new repeats.
        private static List<string> Logs(Pending run)
        {
            var lines = new List<string>();
            if (run.Console == null)
            {
                return lines;
            }

            List<ConsoleBuffer.Entry> entries = run.Console.Query(run.ConsoleId, ConsoleBuffer.Capacity, null);
            bool cut = false;
            foreach (ConsoleBuffer.Entry entry in entries.Take(MaxLogs))
            {
                string first = FirstLine(entry.Text);
                cut |= first.Length > MaxLogText || first != (entry.Text ?? "").Trim();
                int count = entry.Count;
                if (run.Tail != null && entry.Count > run.Tail.Count && entry.Type == run.Tail.Type && entry.Text == run.Tail.Text && entry.Frame == run.Tail.Frame)
                {
                    count -= run.Tail.Count;
                }

                lines.Add("  " + entry.Type + ": " + Clip(first, MaxLogText) + (count > 1 ? " x" + count.ToString(CultureInfo.InvariantCulture) : "")
                    + (entry.Frame == null ? "" : "  " + entry.Frame));
            }

            // A cut text (past 160 characters, or past its first line) adds --whole to the line that names the console.
            string again = "ug console --after " + run.ConsoleId.ToString(CultureInfo.InvariantCulture) + (cut ? " --whole" : "");
            if (entries.Count > MaxLogs)
            {
                lines.Add("  (+" + (entries.Count - MaxLogs).ToString(CultureInfo.InvariantCulture) + " more: " + again + ")");
            }
            else if (cut)
            {
                lines.Add("  (whole text: " + again + ")");
            }

            return lines;
        }

        // `  changed: Main.unity modified, not saved` for scenes the call left modified (edit mode: play-mode changes go anyway).
        private static List<string> Changed(Pending run)
        {
            var lines = new List<string>();
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return lines;
            }

            var names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty && !run.DirtyBefore.Contains(scene.handle))
                {
                    names.Add(string.IsNullOrEmpty(scene.path) ? "the untitled scene" : Path.GetFileName(scene.path));
                }
            }

            if (names.Count > 0)
            {
                lines.Add("  changed: " + string.Join(", ", names) + " modified, not saved");
            }

            return lines;
        }

        private static HashSet<int> DirtyScenes()
        {
            var dirty = new HashSet<int>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    dirty.Add(scene.handle);
                }
            }

            return dirty;
        }

        private static string Message(Exception e) => Clip(FirstLine(e.Message), MaxMessage);

        private static string FirstLine(string text)
        {
            string line = (text ?? "").Replace("\r\n", "\n").Split('\n')[0].Trim();
            return line;
        }

        private static string Clip(string text, int max) => text.Length > max ? text.Substring(0, max - 3) + "..." : text;

        /// <summary>What a call returned that finishes later: an awaitable or an IEnumerator that is not also an IEnumerable.</summary>
        private abstract class Wait
        {
            public object Result;
            public Exception Error;
            public bool HasResult;

            /// <summary>Advances once; true when finished.</summary>
            public abstract bool Step();

            public static Wait For(object value)
            {
                if (value == null)
                {
                    return null;
                }

                MethodInfo getAwaiter = value.GetType().GetMethod("GetAwaiter", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (getAwaiter != null)
                {
                    object awaiter = getAwaiter.Invoke(value, null);
                    PropertyInfo completed = awaiter?.GetType().GetProperty("IsCompleted", BindingFlags.Instance | BindingFlags.Public);
                    MethodInfo result = awaiter?.GetType().GetMethod("GetResult", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                    if (completed != null && completed.PropertyType == typeof(bool) && result != null)
                    {
                        return new Awaited(awaiter, completed, result);
                    }
                }

                // A sequence is a value: the object C# makes for an IEnumerable<T> iterator is its IEnumerator too.
                return value is IEnumerator steps && !(value is IEnumerable) ? new Stepped(steps) : null;
            }
        }

        // Anything with GetAwaiter(): Task, ValueTask, Unity's Awaitable, UniTask. Its continuations run on Unity's main-thread context.
        private sealed class Awaited : Wait
        {
            private readonly object _awaiter;
            private readonly PropertyInfo _completed;
            private readonly MethodInfo _result;

            public Awaited(object awaiter, PropertyInfo completed, MethodInfo result)
            {
                _awaiter = awaiter;
                _completed = completed;
                _result = result;
                HasResult = result.ReturnType != typeof(void) && result.ReturnType.Name != "VoidTaskResult";
            }

            public override bool Step()
            {
                if (!(bool)_completed.GetValue(_awaiter))
                {
                    return false;
                }

                try
                {
                    Result = Values.Materialize(_result.Invoke(_awaiter, null));
                }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    Error = Unwrap(e.InnerException);
                }

                return true;
            }
        }

        // An IEnumerator stepped like an editor coroutine: one MoveNext per update, nested enumerators
        // (CustomYieldInstruction is one) run inside it, AsyncOperation and WaitForSeconds wait.
        private sealed class Stepped : Wait
        {
            private readonly Stack<IEnumerator> _stack = new Stack<IEnumerator>();
            private Func<bool> _waiting;

            public Stepped(IEnumerator steps)
            {
                _stack.Push(steps);
            }

            public int Steps { get; private set; }

            public override bool Step()
            {
                if (_waiting != null)
                {
                    if (!_waiting())
                    {
                        return false;
                    }

                    _waiting = null;
                }

                IEnumerator top = _stack.Peek();
                if (!top.MoveNext())
                {
                    _stack.Pop();
                    return _stack.Count == 0;
                }

                Steps++;
                switch (top.Current)
                {
                    case IEnumerator nested:
                        _stack.Push(nested);
                        break;
                    case AsyncOperation operation:
                        _waiting = () => operation.isDone;
                        break;
                    case WaitForSeconds seconds when _waitSeconds != null:
                        var clock = Stopwatch.StartNew();
                        float wait = (float)_waitSeconds.GetValue(seconds);
                        _waiting = () => clock.Elapsed.TotalSeconds >= wait;
                        break;
                }

                return false;
            }
        }
    }
}
