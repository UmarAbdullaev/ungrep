using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>The uGUI side of play-input, set by the Ugui assembly when com.unity.ugui is present.</summary>
    internal interface IUiInput
    {
        /// <summary>The `ug ui` lines.</summary>
        Reply List(bool all);

        /// <summary>`ug click`: an element address or label, or `x,y` in Game view pixels (y down); a value operates the component.</summary>
        Reply Click(string element, string value);
    }

    /// <summary>
    /// The play-input requests (specs/SPEC-play-input.md): `ug ui` and `ug click` here, the device
    /// actions beside them. Play mode only, nothing saved, no Undo: every action is a runtime event.
    /// </summary>
    internal static class PlayInput
    {
        internal static IUiInput Ui { get; set; }

        public static Reply UiList(Request request)
        {
            Reply refused = NeedPlaying(false) ?? NoUgui();
            return refused ?? Ui.List(request.Get("all") == "true");
        }

        public static Func<Reply> UiClick(Request request)
        {
            string element = request.Get("element");
            Reply refused = NeedPlaying(false) ?? NoUgui();
            if (refused == null && string.IsNullOrWhiteSpace(element))
            {
                refused = Answer.Error("ug click needs an element: an address or label from ug ui, or x,y");
            }

            Reply done = refused ?? Ui.Click(element.Trim(), request.Get("value"));
            return () => done;
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

        private static Reply NoUgui() =>
            Ui == null ? Answer.Error("com.unity.ugui is not in this project: ug ui and ug click need it") : null;
    }
}
