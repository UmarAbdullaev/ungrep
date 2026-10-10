using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>Test runs through the Unity Test Framework; set by the TestRuns assembly when that package is present.</summary>
    internal interface ITestRuns
    {
        /// <summary>Starts a run and returns its id, or null with <paramref name="error"/>.</summary>
        string Start(string platform, string filter, out string error);

        /// <summary>The run's answer as JSON, or null for an unknown id.</summary>
        string Result(string id);
    }

    /// <summary>
    /// Starts the bridge after every domain reload (in batch mode only with `-ugBridge`), stops it
    /// before the next one, and routes requests. See specs/SPEC-editor-link.md.
    /// </summary>
    [InitializeOnLoad]
    internal static class Bridge
    {
        public const string Version = "0.6.0";

        private const string PortKey = "unitygraph.bridge.port";
        private const string TokenKey = "unitygraph.bridge.token";
        private const string ConsoleKey = "unitygraph.bridge.console";
        private const int DiscoveryAttempts = 40;

        private static readonly ConsoleBuffer _console = new ConsoleBuffer();
        private static Server _server;
        private static readonly PlayLog _playLog = new PlayLog();
        private static CompileLog _compile;
        private static volatile string _status = "{}";
        private static string _statusInputs;

        static Bridge()
        {
            if (Application.isBatchMode && Array.IndexOf(Environment.GetCommandLineArgs(), "-ugBridge") < 0)
            {
                return;
            }

            try
            {
                Start();
            }
            catch (Exception e)
            {
                // A listener already started would hold its port with nothing to stop it before the next reload.
                _server?.Stop();
                _server = null;
                UnityEngine.Debug.LogWarning("unitygraph bridge did not start: " + e.Message);
            }
        }

        internal static ITestRuns TestRuns { get; set; }

        internal static string ProjectRoot { get; } = Path.GetDirectoryName(Application.dataPath);

        private static string Folder => Path.Combine(ProjectRoot, "Library", "unitygraph");

        private static string DiscoveryFile => Path.Combine(Folder, "bridge.json");

        private static void Start()
        {
            _console.Restore(SessionState.GetString(ConsoleKey, ""));
            SessionState.EraseString(ConsoleKey);
            _playLog.Load();
            Application.logMessageReceivedThreaded += OnLog;
            _compile = new CompileLog(ProjectRoot, Folder);
            _compile.Attach();

            string token = SessionState.GetString(TokenKey, "");
            if (token.Length == 0)
            {
                token = Guid.NewGuid().ToString("N");
                SessionState.SetString(TokenKey, token);
            }

            PlayInput.Console = _console;
            RefreshStatus();
            _server = new Server(token, () => _status);
            _server.Start(SessionState.GetInt(PortKey, 0));
            SessionState.SetInt(PortKey, _server.Port);
            WriteDiscovery(token, _server.Port);

            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.pauseStateChanged += OnPauseChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Quit;
        }

        private static void Stop()
        {
            InputQueue.Abort("assemblies reloaded");
            PlayInput.Devices?.Reset();
            EditorApplication.update -= Update;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.pauseStateChanged -= OnPauseChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            Application.logMessageReceivedThreaded -= OnLog;
            _compile?.Detach();
            _server?.Stop();
            SessionState.SetString(ConsoleKey, _console.Save());
        }

        private static void Quit()
        {
            Stop();
            try
            {
                File.Delete(DiscoveryFile);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static void Update()
        {
            EvalGuard.Beat();
            RefreshStatus();
            CodeRun.Tick();
            _server.Pump(Handle);
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            SceneOps.OnPlayModeChanged(change, _console.LastId);
            _playLog.OnPlayModeChanged(change, DateTime.UtcNow, _console.LastId);
            _playLog.Persist();
            InputQueue.OnPlayModeChanged(change);
        }

        private static void OnPauseChanged(PauseState state)
        {
            _playLog.OnPause(state == PauseState.Paused, DateTime.UtcNow);
            _playLog.Persist();
        }

        private static void OnLog(string condition, string stackTrace, LogType type) =>
            _console.Add(ConsoleBuffer.TypeName(type), condition, StackFrames.OwnFrame(stackTrace, ProjectRoot));

        private static void RefreshStatus()
        {
            string state = EditorApplication.isCompiling ? "compiling"
                : EditorApplication.isUpdating ? "updating"
                : EditorApplication.isPlaying ? EditorApplication.isPaused ? "paused" : "playing"
                : "idle";
            long console = _console.LastId;
            string inputs = state + "|" + _compile.Count + "|" + _compile.Errors + "|" + _compile.Warnings + "|" + console;
            if (inputs == _statusInputs)
            {
                return;
            }

            _statusInputs = inputs;
            _status = new Json().Object()
                .Field("state", state)
                .Field("unity", Application.unityVersion)
                .Field("bridge", Version)
                .Field("compile", _compile.Count)
                .Field("errors", _compile.Errors)
                .Field("warnings", _compile.Warnings)
                .Field("console", console)
                .EndObject().ToString();
        }

        private static Func<Reply> Handle(Request request)
        {
            switch (request.Path)
            {
                case "/refresh":
                    return Refresh();
                case "/compile":
                    return Now(Reply.Ok(_compile.Answer));
                case "/console":
                    return Now(ConsoleEntries(request));
                case "/playlog":
                    return Now(Reply.Ok(_playLog.Answer()));
                case "/tests":
                    return Now(StartTests(request));
                case "/tests/result":
                    return Now(TestResult(request));
                default:
                    return Routes.Handle(request, _console) ?? Now(Reply.Fail(404, "unknown request " + request.Path));
            }
        }

        private static Func<Reply> Now(Reply reply) => () => reply;

        // Unity can flag a compile an update or two after the refresh, so the answer waits up to
        // two updates before saying nothing compiles.
        private static Func<Reply> Refresh()
        {
            int before = _compile.Count;
            AssetDatabase.Refresh();
            if (before == 0 && EditorUtility.scriptCompilationFailed)
            {
                // The failing compile happened before the bridge loaded: compile again to see its errors.
                CompilationPipeline.RequestScriptCompilation();
            }

            int updates = 0;
            return () =>
            {
                bool compiling = EditorApplication.isCompiling || _compile.Count != before;
                if (!compiling && ++updates <= 2)
                {
                    return null;
                }

                return Reply.Ok(new Json().Object().Field("compiling", compiling).Field("compile", before).EndObject().ToString());
            };
        }

        private static Reply ConsoleEntries(Request request)
        {
            long after = long.TryParse(request.Get("after"), NumberStyles.None, CultureInfo.InvariantCulture, out long a) ? a : 0;
            int limit = int.TryParse(request.Get("limit"), NumberStyles.None, CultureInfo.InvariantCulture, out int l) ? Math.Max(1, Math.Min(l, 500)) : 50;
            string types = request.Get("types");
            HashSet<string> wanted = string.IsNullOrEmpty(types) ? null : new HashSet<string>(types.Split(','), StringComparer.Ordinal);

            var json = new Json().Object().Field("last", _console.LastId).Key("entries").Array();
            foreach (ConsoleBuffer.Entry entry in _console.Query(after, limit, wanted))
            {
                ConsoleBuffer.Write(json, entry);
            }

            return Reply.Ok(json.EndArray().EndObject().ToString());
        }

        private static Reply StartTests(Request request)
        {
            if (TestRuns == null)
            {
                return Reply.Fail(501, "com.unity.test-framework is not in this project");
            }

            string id = TestRuns.Start(request.Get("platform") ?? "EditMode", request.Get("filter"), out string error);
            return id == null ? Reply.Fail(409, error) : Reply.Ok(new Json().Object().Field("id", id).EndObject().ToString());
        }

        private static Reply TestResult(Request request)
        {
            string answer = TestRuns == null ? null : TestRuns.Result(request.Get("id") ?? "");
            return answer == null ? Reply.Fail(404, "no test run " + request.Get("id")) : Reply.Ok(answer);
        }

        private static void WriteDiscovery(string token, int port)
        {
            string json = new Json().Object()
                .Field("bridge", Version)
                .Field("port", port)
                .Field("token", token)
                .Field("pid", Process.GetCurrentProcess().Id)
                .Field("unity", Application.unityVersion)
                .EndObject().ToString();
            Directory.CreateDirectory(Folder);
            Publish(DiscoveryFile, json);
        }

        /// <summary>Writes <paramref name="path"/> whole through a temporary file, so a reader never sees half of it.</summary>
        internal static void Publish(string path, string text)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);

            // A reader (an older ug, a virus scanner) can hold the file for a moment: a failed replace
            // here left the bridge unstarted until the next reload, so try for up to a second.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temp, path, null);
                    }
                    else
                    {
                        File.Move(temp, path);
                    }

                    return;
                }
                catch (IOException) when (attempt < DiscoveryAttempts)
                {
                    System.Threading.Thread.Sleep(25);
                }
            }
        }
    }
}
