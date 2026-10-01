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
        public const string Version = "0.4.0";

        private const string PortKey = "unitygraph.bridge.port";
        private const string TokenKey = "unitygraph.bridge.token";
        private const string ConsoleKey = "unitygraph.bridge.console";

        private static readonly ConsoleBuffer _console = new ConsoleBuffer();
        private static Server _server;
        private static readonly PlayLog _playLog = new PlayLog();
        private static CompileLog _compile;
        private static volatile string _status = "{}";
        private static string _statusInputs;
        private static HashSet<string> _own;

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
            RefreshStatus();
            _server.Pump(Handle);
        }

        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            SceneOps.OnPlayModeChanged(change, _console.LastId);
            _playLog.OnPlayModeChanged(change, DateTime.UtcNow, _console.LastId);
            _playLog.Persist();
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
                case "/run":
                    return Now(Run(request));
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

        private static Reply Run(Request request)
        {
            MethodRunner.Outcome outcome = MethodRunner.Run(request.Get("target"), IsOwnAssembly, ProjectRoot);
            return Reply.Ok(new Json().Object()
                .Field("ran", outcome.Ran)
                .Field("result", outcome.Result)
                .Field("error", outcome.Error)
                .Field("frame", outcome.Frame)
                .Field("ms", Math.Round(outcome.Milliseconds, 1))
                .EndObject().ToString());
        }

        // Script assemblies built from files under Assets/ or embedded packages.
        private static bool IsOwnAssembly(string name)
        {
            if (_own == null)
            {
                _own = new HashSet<string>(
                    CompilationPipeline.GetAssemblies()
                        .Where(a => !MethodRunner.IsFramework(a.name) && a.sourceFiles.Any(f => StackFrames.IsOwn(f.Replace('\\', '/'), ProjectRoot)))
                        .Select(a => a.name),
                    StringComparer.Ordinal);
            }

            return _own.Contains(name);
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
            string temp = DiscoveryFile + ".tmp";
            File.WriteAllText(temp, json);
            if (File.Exists(DiscoveryFile))
            {
                File.Replace(temp, DiscoveryFile, null);
            }
            else
            {
                File.Move(temp, DiscoveryFile);
            }
        }
    }
}
