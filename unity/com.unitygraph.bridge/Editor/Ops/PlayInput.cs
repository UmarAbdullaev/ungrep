using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>The device side of play-input, set by the InputSystem assembly when com.unity.inputsystem is present.</summary>
    internal interface IDeviceInput
    {
        /// <summary>A key, gamepad button (`pad:south`) or mouse button (`mouse:left`) held for <paramref name="hold"/> seconds; null and an error for a name it does not know.</summary>
        PlayAction Press(string name, double hold, out string error);

        /// <summary>A gamepad stick pushed to x,y (y up) for <paramref name="hold"/> seconds.</summary>
        PlayAction Stick(bool left, float x, float y, double hold);

        /// <summary>A touch (or the left mouse button) down and up at a Game view point (y down).</summary>
        PlayAction Tap(float x, float y, double hold, bool mouse);

        /// <summary>A touch (or the left mouse button) down, moved in a line over <paramref name="over"/> seconds, up.</summary>
        PlayAction Swipe(float x1, float y1, float x2, float y2, double over, bool mouse);

        /// <summary>Removes the devices the bridge added and puts the input settings back; called when play stops.</summary>
        void Reset();
    }

    /// <summary>
    /// The play-input requests (specs/SPEC-play-input.md): the device actions here, `ug ui`, `ug click`
    /// and `ug nav` in PlayInput.Ui.cs. Play mode only, nothing saved, no Undo: every action is a runtime event.
    /// </summary>
    internal static partial class PlayInput
    {
        internal static IUiInput Ui { get; set; }

        internal static IDeviceInput Devices { get; set; }

        /// <summary>The bridge's console buffer, for the digest of what an action logged; set by the bridge.</summary>
        internal static ConsoleBuffer Console { get; set; }

        public static Func<Reply> Press(Request request)
        {
            string key = request.Get("key");
            if (string.IsNullOrWhiteSpace(key))
            {
                return Refuse("ug press needs a key or button: Space, Enter, pad:south, mouse:left");
            }

            if (!Range(request, "hold", 0.1, 0.05, 60, "--hold", out double hold, out Reply bad))
            {
                return Refuse(bad);
            }

            return Device("press", (IDeviceInput devices, out string error) => devices.Press(key.Trim(), hold, out error));
        }

        public static Func<Reply> Stick(Request request)
        {
            string side = request.Get("side");
            if (side != "left" && side != "right")
            {
                return Refuse("ug stick needs left or right");
            }

            if (!Range(request, "x", 0, -1, 1, "x,y", out double x, out Reply bad) || !Range(request, "y", 0, -1, 1, "x,y", out double y, out bad)
                || !Range(request, "hold", 0.5, 0.05, 60, "--hold", out double hold, out bad))
            {
                return Refuse(bad);
            }

            return Device("stick", (IDeviceInput devices, out string error) =>
            {
                error = null;
                return devices.Stick(side == "left", (float)x, (float)y, hold);
            });
        }

        public static Func<Reply> Tap(Request request)
        {
            if (!Point(request, "x", "y", out float x, out float y, out Reply bad) || !Range(request, "hold", 0.1, 0.05, 60, "--hold", out double hold, out bad))
            {
                return Refuse(bad);
            }

            bool mouse = request.Get("mouse") == "true";
            return Device("tap", (IDeviceInput devices, out string error) =>
            {
                error = null;
                return devices.Tap(x, y, hold, mouse);
            });
        }

        public static Func<Reply> Swipe(Request request)
        {
            if (!Point(request, "x1", "y1", out float x1, out float y1, out Reply bad) || !Point(request, "x2", "y2", out float x2, out float y2, out bad)
                || !Range(request, "over", 0.3, 0.05, 10, "--over", out double over, out bad))
            {
                return Refuse(bad);
            }

            bool mouse = request.Get("mouse") == "true";
            return Device("swipe", (IDeviceInput devices, out string error) =>
            {
                error = null;
                return devices.Swipe(x1, y1, x2, y2, over, mouse);
            });
        }

        /// <summary>`ug wait`: the game runs for a while; nothing is sent to it.</summary>
        public static Func<Reply> Wait(Request request)
        {
            if (!Range(request, "seconds", double.NaN, 0.05, 60, "seconds", out double seconds, out Reply bad))
            {
                return Refuse(bad);
            }

            Reply refused = NeedPlaying(true);
            if (refused != null)
            {
                return () => refused;
            }

            var action = new PlayAction("wait")
            {
                Start = () => Answer.Line("waited " + InputQueue.Seconds(seconds)),
                Hold = PlayAction.Lasting(seconds, 0),
                Settle = false,
            };
            return InputQueue.Start(action);
        }

        /// <summary>`ug play --step`: frames advanced one at a time while the editor is paused.</summary>
        public static Func<Reply> Step(Request request)
        {
            if (!Range(request, "frames", 1, 1, 600, "frames", out double count, out Reply bad) || count != Math.Floor(count))
            {
                return Refuse(bad ?? Answer.Error("frames is a whole number, 1 to 600"));
            }

            Reply refused = NeedPlaying(false) ?? (EditorApplication.isPaused ? null : Answer.Error("not paused: ug pause first"));
            if (refused != null)
            {
                return () => refused;
            }

            int frames = (int)count;
            var action = new PlayAction("step") { Settle = false, RunsPaused = true };
            int done = 0;
            int atStep = 0;
            double sent = 0;
            bool pending = false;
            int fires = ErrorPause.Current?.Fires ?? 0;
            action.Start = () => Answer.Line("stepped");
            action.Hold = (elapsed, passed) =>
            {
                if (pending && Time.frameCount != atStep)
                {
                    done++;
                    pending = false;
                }

                bool resumed = !EditorApplication.isPaused;
                bool caught = (ErrorPause.Current?.Fires ?? 0) != fires;
                if (done >= frames || resumed || (caught && !pending))
                {
                    string text = done == 1 ? "1 frame" : done + " frames";
                    action.Result = Answer.Line(done < frames
                        ? "stepped " + text + " of " + frames + " (frame " + Time.frameCount + "): " + (resumed ? "the editor was resumed" : "the first error stopped it")
                        : "stepped " + text + " (frame " + Time.frameCount + ")");
                    return true;
                }

                if (!pending)
                {
                    atStep = Time.frameCount;
                    sent = EditorApplication.timeSinceStartup;
                    pending = true;
                    EditorApplication.Step();
                }
                else if (EditorApplication.timeSinceStartup - sent > 5)
                {
                    throw new InvalidOperationException("the editor did not advance a frame in 5 s (frame " + atStep + ")");
                }

                return false;
            };
            return InputQueue.Start(action);
        }

        // Refusals in order: play state, the Input System (not in the project, or not read), then the action's own name check.
        private delegate PlayAction Make(IDeviceInput devices, out string error);

        private static Func<Reply> Device(string command, Make make)
        {
            Reply refused = NeedPlaying(true) ?? NoDevices(command);
            if (refused == null)
            {
                PlayAction action = make(Devices, out string error);
                if (action != null)
                {
                    return InputQueue.Start(action);
                }

                refused = Answer.Error(error);
            }

            return () => refused;
        }

        private static Reply NoDevices(string command)
        {
            if (Devices == null)
            {
                return Answer.Error("com.unity.inputsystem is not in this project: ug " + command + " needs it; ug click still works");
            }

#if !ENABLE_INPUT_SYSTEM
            return Answer.Error("this project reads the old Input Manager (Player settings, Active Input Handling): ug " + command + " needs the Input System or Both; ug click still works");
#else
            return null;
#endif
        }

        private static Func<Reply> Refuse(string error) => Refuse(Answer.Error(error));

        private static Func<Reply> Refuse(Reply reply) => () => reply;

        // `name` from the query as a number in min..max (the fallback when absent; NaN means required).
        private static bool Range(Request request, string name, double fallback, double min, double max, string what, out double value, out Reply error)
        {
            error = null;
            string text = request.Get(name);
            if (text == null && !double.IsNaN(fallback))
            {
                value = fallback;
                return true;
            }

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
            {
                error = Answer.Error(what + " must be a number, not " + (text ?? "nothing"));
                return false;
            }

            if (value < min || value > max)
            {
                string limits = what == "x,y" ? "-1 to 1, like 1,0" : min.ToString(CultureInfo.InvariantCulture) + " to " + max.ToString(CultureInfo.InvariantCulture);
                error = Answer.Error(what == "x,y" ? "x,y must be " + limits : what + " must be " + limits + ", not " + text);
                return false;
            }

            return true;
        }

        // A point on screen, in Game view pixels with y down.
        private static bool Point(Request request, string xName, string yName, out float x, out float y, out Reply error)
        {
            x = 0;
            y = 0;
            error = null;
            string text = request.Get(xName) + "," + request.Get(yName);
            if (!TryPoint(text, out x, out y))
            {
                error = Answer.Error("a point is x,y in Game view pixels, like 640,360 (y down from the top)");
                return false;
            }

            if (!ScreenSize(out int width, out int height))
            {
                error = Answer.Error("the Game view has no size (this editor runs without a window): ug needs one to place a touch");
                return false;
            }

            if (x < 0 || y < 0 || x > width || y > height)
            {
                error = Answer.Error("the point " + Math.Round(x) + "," + Math.Round(y) + " is off screen (the Game view is " + width + "x" + height + ")");
                return false;
            }

            return true;
        }

        /// <summary>The refusal for an editor that is not playing, or (when <paramref name="running"/>) not running frames.</summary>
        public static Reply NeedPlaying(bool running)
        {
            if (!EditorApplication.isPlaying)
            {
                return Answer.Error("not playing: ug play first");
            }

            return running && EditorApplication.isPaused
                ? Answer.Error("paused: input is read on frames; ug play resumes, ug play --step <n> advances")
                : null;
        }

        /// <summary>The Game view's size in pixels, or false when the editor has none (a headless editor).</summary>
        public static bool ScreenSize(out int width, out int height)
        {
            width = Screen.width;
            height = Screen.height;
            return width > 0 && height > 0;
        }

        /// <summary>`x,y` as two numbers, else false.</summary>
        public static bool TryPoint(string text, out float x, out float y)
        {
            x = 0;
            y = 0;
            string[] parts = text.Split(',');
            return parts.Length == 2
                && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }
    }
}
