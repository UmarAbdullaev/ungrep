using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ungrep.Bridge
{
    /// <summary>
    /// `ug perf frame-debugger` (SPEC-editor-ops, Profiler): Unity's Frame Debugger through its
    /// internal API, by reflection. The names are Unity 6000.3's and are checked on every request,
    /// so another Unity refuses by name instead of failing halfway. With the Frame Debugger window
    /// open, the window's own enable, disable and event limit are used (it throws when the utility
    /// changes under it); otherwise no window is opened.
    /// </summary>
    internal static class FrameDebuggerOps
    {
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string UtilityName = "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility";
        private const string DataName = "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData";
        private const string WindowName = "UnityEditor.FrameDebuggerWindow";
        private const int ListCap = 50;

        // Why there may be no events: measured in a windowed 6000.3 editor, which drew nothing for
        // the Frame Debugger while another application had the focus.
        private const string Capture = "Unity captures the next frame the Game or Scene view draws, and an editor in the background "
            + "draws none: click its window, then ug perf frame-debugger events";

        private static Type _utility;
        private static Type _data;
        private static Type _window;

        /// <summary>`/perf/frame-debugger?do=enable|events|disable&amp;event=&amp;from=&amp;top=`.</summary>
        public static Func<Reply> Handle(Request request)
        {
            string missing = Missing();
            if (missing != null)
            {
                return Now(Answer.Error("the Frame Debugger's internals are not the ones ug knows (no " + missing + " in Unity " + Application.unityVersion
                    + "): ug perf frame-debugger needs Unity 6000.3's"));
            }

            try
            {
                switch (request.Get("do"))
                {
                    case "enable":
                        return Guarded(Enable());
                    case "disable":
                        return Now(Disable());
                    case "events":
                        return Guarded(Events(request));
                    default:
                        return Now(Answer.Error("do is enable, events or disable"));
                }
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                return Now(Threw(e.InnerException));
            }
        }

        // A reflected call that throws answers Unity's own exception, not TargetInvocationException.
        private static Func<Reply> Guarded(Func<Reply> poll) => () =>
        {
            try
            {
                return poll();
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                return Threw(e.InnerException);
            }
        };

        private static Reply Threw(Exception e) => Answer.Error("Unity's Frame Debugger threw " + e.GetType().Name + ": " + e.Message);

        /// <summary>The first member ug needs that this Unity lacks, or null.</summary>
        internal static string Missing()
        {
            Assembly editor = typeof(Editor).Assembly;
            _utility = _utility ?? editor.GetType(UtilityName);
            _data = _data ?? editor.GetType(DataName);
            _window = _window ?? editor.GetType(WindowName);
            if (_utility == null)
            {
                return "FrameDebuggerUtility";
            }

            if (_data == null)
            {
                return "FrameDebuggerEventData";
            }

            if (_window == null)
            {
                return "FrameDebuggerWindow";
            }

            foreach (string property in new[] { "locallySupported", "count", "limit" })
            {
                if (_utility.GetProperty(property, Static) == null)
                {
                    return "FrameDebuggerUtility." + property;
                }
            }

            var utility = new[] { ("SetEnabled", 2), ("GetFrameEvents", 0), ("GetFrameEventInfoName", 1), ("GetFrameEventData", 2), ("GetBatchBreakCauseStrings", 0) };
            foreach ((string name, int count) in utility)
            {
                if (Method(_utility, Static, name, count) == null)
                {
                    return "FrameDebuggerUtility." + name + " with " + Answer.Count(count, "parameter", "parameters");
                }
            }

            foreach ((string name, int count) in new[] { ("EnableFrameDebugger", 0), ("DisableFrameDebugger", 0), ("ChangeFrameEventLimit", 1) })
            {
                if (Method(_window, Instance, name, count) == null)
                {
                    return "FrameDebuggerWindow." + name + " with " + Answer.Count(count, "parameter", "parameters");
                }
            }

            return null;
        }

        // Unity overloads some of these; ug's is the one with this many parameters.
        private static MethodInfo Method(Type type, BindingFlags flags, string name, int parameters) =>
            type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameters);

        private static void OnWindow(EditorWindow window, string method, params object[] arguments) =>
            Method(_window, Instance, method, arguments.Length).Invoke(window, arguments);

        private static Func<Reply> Enable()
        {
            if (!(bool)Get("locallySupported"))
            {
                return Now(Answer.Error("Unity's Frame Debugger is not available in this editor: it renders "
                    + SystemInfo.renderingThreadingMode + " (as -batchmode and -automated start it); start Unity with -force-gfx-mt"));
            }

            bool paused = false;
            if (!FrameDebugger.enabled)
            {
                if (EditorApplication.isPlaying && !EditorApplication.isPaused)
                {
                    EditorApplication.isPaused = true;
                    paused = true;
                }

                EditorWindow window = OpenWindow();
                if (window != null)
                {
                    OnWindow(window, "EnableFrameDebugger");
                }
                else
                {
                    SetEnabled(true);
                }
            }

            double start = EditorApplication.timeSinceStartup;
            double steady = start;
            int seen = -1;
            return () =>
            {
                double now = EditorApplication.timeSinceStartup;
                int count = (int)Get("count");
                if (count != seen)
                {
                    seen = count;
                    steady = now;
                }

                if ((count == 0 || now - steady < 0.3) && now - start < 5)
                {
                    Repaint();
                    return null;
                }

                if (!FrameDebugger.enabled)
                {
                    return Answer.Error("Unity did not turn the Frame Debugger on");
                }

                return Answer.Line("frame debugger on: " + (count == 0 ? "no events yet" : Answer.Count(count, "event", "events"))
                    + (paused ? " (play mode paused; ug play resumes)" : EditorApplication.isPaused ? " (play mode paused)" : "")
                    + (count == 0 ? ": " + Capture : "; ug perf frame-debugger events"));
            };
        }

        private static Reply Disable()
        {
            if (!FrameDebugger.enabled)
            {
                return Answer.Error("the frame debugger is off");
            }

            EditorWindow window = OpenWindow();
            if (window != null)
            {
                OnWindow(window, "DisableFrameDebugger");
            }
            else
            {
                SetEnabled(false);
            }

            return Answer.Line("frame debugger off" + (EditorApplication.isPlaying && EditorApplication.isPaused ? "; play mode stays paused (ug play resumes)" : ""));
        }

        private static Func<Reply> Events(Request request)
        {
            if (!FrameDebugger.enabled)
            {
                return Now(Answer.Error("the frame debugger is off: ug perf frame-debugger enable"));
            }

            int count = (int)Get("count");
            Array events = (Array)Call("GetFrameEvents");
            int length = Math.Min(count, events?.Length ?? 0);
            if (length == 0)
            {
                return Now(Answer.Error("the frame debugger holds no events yet: " + Capture));
            }

            string single = request.Get("event");
            if (single != null)
            {
                if (!int.TryParse(single, out int index) || index < 0 || index >= length)
                {
                    return Now(Answer.Error("event is 0 to " + (length - 1) + " (" + Answer.Count(length, "event", "events") + ")"));
                }

                return Detail(events, index);
            }

            if (!Whole(request.Get("from"), 0, int.MaxValue, 0, out int from) || !Whole(request.Get("top"), 1, 500, ListCap, out int top))
            {
                return Now(Answer.Error("--from is 0 or more, --top 1 to 500"));
            }

            var lines = new List<string> { Answer.Count(length, "event", "events") + " in the frame (index, type, name, object):" };
            for (int i = from; i < Math.Min(length, from + top); i++)
            {
                lines.Add("  " + Line(events, i));
            }

            if (from + top < length)
            {
                lines.Add("  ... " + (length - from - top) + " more: ug perf frame-debugger events --from " + (from + top) + (top == ListCap ? "" : " --top " + top));
            }

            return Now(Answer.Lines(lines));
        }

        // One event: the limit set to it (the Game view shows the frame up to it), then its data once Unity has it.
        private static Func<Reply> Detail(Array events, int index)
        {
            EditorWindow window = OpenWindow();
            if (window != null)
            {
                OnWindow(window, "ChangeFrameEventLimit", index + 1);
            }
            else
            {
                _utility.GetProperty("limit", Static).SetValue(null, index + 1);
            }

            object data = Activator.CreateInstance(_data, true);
            double start = EditorApplication.timeSinceStartup;
            return () =>
            {
                bool ready = (bool)Call("GetFrameEventData", index, data);
                if (!ready && EditorApplication.timeSinceStartup - start < 3)
                {
                    Repaint();
                    return null;
                }

                var lines = new List<string> { "event " + Line(events, index) };
                if (!ready)
                {
                    lines.Add("  Unity gave no detail for it in 3 s");
                    return Answer.Lines(lines);
                }

                string real = Field(data, "m_RealShaderName");
                string original = Field(data, "m_OriginalShaderName");
                string compute = Field(data, "m_ComputeShaderName");
                if (!string.IsNullOrEmpty(real))
                {
                    lines.Add("  shader  " + real + (!string.IsNullOrEmpty(original) && original != real ? " (from " + original + ")" : "")
                        + Join("  pass ", Field(data, "m_PassName")) + Join(" (", Field(data, "m_PassLightMode"), ")"));
                }

                if (!string.IsNullOrEmpty(compute))
                {
                    lines.Add("  compute  " + compute + Join("  kernel ", Field(data, "m_ComputeShaderKernelName")));
                }

                string keywords = Keywords(data);
                if (keywords.Length > 0)
                {
                    lines.Add("  keywords  " + keywords);
                }

                Object mesh = Value(data, "m_Mesh") as Object;
                lines.Add("  " + (mesh == null ? "" : "mesh " + mesh.name + "  ") + "vertices " + Field(data, "m_VertexCount") + ", indices " + Field(data, "m_IndexCount")
                    + ", instances " + Field(data, "m_InstanceCount") + ", draw calls " + Field(data, "m_DrawCallCount"));
                string target = Field(data, "m_RenderTargetName");
                if (!string.IsNullOrEmpty(target))
                {
                    // Unity names temporary targets with their size (`TempBuffer 14 613x428`): not twice.
                    string size = Field(data, "m_RenderTargetWidth") + "x" + Field(data, "m_RenderTargetHeight");
                    lines.Add("  target  " + (target.EndsWith(" " + size, StringComparison.Ordinal) ? target : target + " " + size) + " " + Format(data)
                        + " (" + Answer.Count(Number(Value(data, "m_RenderTargetCount")), "target", "targets") + ")");
                }

                int cause = Number(Value(data, "m_BatchBreakCause"));
                string[] causes = Call("GetBatchBreakCauseStrings") as string[];
                if (cause > 0 && causes != null && cause < causes.Length)
                {
                    lines.Add("  batch break  " + causes[cause]);
                }

                return Answer.Lines(lines);
            };
        }

        // `12  Mesh  Draw Mesh Hero  Main:Player/Model`
        private static string Line(Array events, int index)
        {
            object e = events.GetValue(index);
            object type = e.GetType().GetField("m_Type", Instance)?.GetValue(e);
            Object target = e.GetType().GetField("m_Obj", Instance)?.GetValue(e) as Object;
            string name = Call("GetFrameEventInfoName", index) as string;
            return index + "  " + type + (string.IsNullOrEmpty(name) ? "" : "  " + name) + (target == null ? "" : "  " + Where(target));
        }

        private static string Where(Object target)
        {
            if (EditorUtility.IsPersistent(target))
            {
                return AssetDatabase.GetAssetPath(target) is string path && path.Length > 0 ? path : target.name;
            }

            GameObject owner = target as GameObject ?? (target as Component)?.gameObject;
            return owner != null && owner.scene.IsValid() ? Addresses.Of(owner) : target.name;
        }

        private static string Keywords(object data)
        {
            object value = Value(data, "shaderKeywords") ?? Value(data, "m_ShaderKeywords");
            if (value is string text)
            {
                return text.Trim();
            }

            return value is System.Collections.IEnumerable list ? string.Join(" ", list.Cast<object>().Select(k => k.ToString())) : "";
        }

        private static string Format(object data)
        {
            object format = Value(data, "m_RenderTargetFormat");
            return format == null ? "" : Enum.IsDefined(typeof(UnityEngine.Experimental.Rendering.GraphicsFormat), Number(format))
                ? ((UnityEngine.Experimental.Rendering.GraphicsFormat)Number(format)).ToString() : format.ToString();
        }

        private static EditorWindow OpenWindow() => Resources.FindObjectsOfTypeAll(_window).FirstOrDefault() as EditorWindow;

        private static void SetEnabled(bool on) => Call("SetEnabled", on, ProfilerDriver.connectedProfiler);

        private static void Repaint()
        {
            InternalEditorUtility.RepaintAllViews();
            if (!EditorApplication.isPlaying || EditorApplication.isPaused)
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        private static object Get(string property) => _utility.GetProperty(property, Static).GetValue(null);

        private static object Call(string method, params object[] arguments) =>
            Method(_utility, Static, method, arguments.Length).Invoke(null, arguments);

        private static object Value(object data, string field) => data.GetType().GetField(field, Instance)?.GetValue(data);

        private static string Field(object data, string field) => Value(data, field)?.ToString() ?? "";

        private static int Number(object value)
        {
            try
            {
                return value == null ? 0 : Convert.ToInt32(value);
            }
            catch (Exception e) when (e is InvalidCastException || e is OverflowException || e is FormatException)
            {
                return 0;
            }
        }

        private static string Join(string before, string value, string after = "") => string.IsNullOrEmpty(value) ? "" : before + value + after;

        private static bool Whole(string text, int min, int max, int fallback, out int value)
        {
            value = fallback;
            return text == null || (int.TryParse(text, out value) && value >= min && value <= max);
        }

        private static Func<Reply> Now(Reply reply) => () => reply;
    }
}
