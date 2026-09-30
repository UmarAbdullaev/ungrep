using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace UnityGraph.EditorLink;

/// <summary>What <see cref="BridgeClient.Connect"/> found: a client and the editor's status, or why there is none.</summary>
public sealed record BridgeConnection(BridgeClient? Client, BridgeStatus? Status, string? Problem);

/// <summary>
/// Talks to the bridge of a running editor over loopback (specs/SPEC-editor-link.md). Every call
/// is bounded by a timeout and fails with <see cref="BridgeUnavailableException"/>, never hangs.
/// </summary>
public sealed class BridgeClient : IDisposable
{
    /// <summary>The oldest bridge this client understands.</summary>
    public const string MinimumBridge = "0.1.0";

    /// <summary>The oldest bridge with the editor-ops requests (SPEC-editor-ops).</summary>
    public const string MinimumOpsBridge = "0.2.0";

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    // The bridge's listener thread accepts at once even while the editor is busy, but Windows
    // retries a refused loopback connect for about 2 s: a short connect timeout means refused.
    private static readonly TimeSpan _connectTimeout = TimeSpan.FromMilliseconds(500);

    private readonly string _projectRoot;
    private readonly Func<int, bool> _isAlive;
    private readonly HttpClient _http;
    private BridgeInfo _info;

    private BridgeClient(string projectRoot, BridgeInfo info, Func<int, bool> isAlive)
    {
        _projectRoot = projectRoot;
        _info = info;
        _isAlive = isAlive;
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = _connectTimeout })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public BridgeInfo Info => _info;

    /// <summary>Whether the bridge answers editor-ops requests.</summary>
    public bool HasOps => Version.TryParse(_info.Bridge, out Version? version) && version >= Version.Parse(MinimumOpsBridge);

    /// <summary>How long the compile and play-mode waits sleep between status polls.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public static string DiscoveryPath(string projectRoot) => Path.Combine(projectRoot, "Library", "unitygraph", "bridge.json");

    /// <summary>
    /// Reads `bridge.json`, checks the editor process is alive and the bridge new enough, and asks
    /// for its status. <paramref name="isAlive"/> replaces the process check in tests.
    /// </summary>
    public static BridgeConnection Connect(string projectRoot, Func<int, bool>? isAlive = null)
    {
        isAlive ??= IsUnityRunning;
        BridgeInfo? info = ReadDiscovery(projectRoot, out string? problem);
        if (info == null)
        {
            return new BridgeConnection(null, null, problem);
        }

        if (!isAlive(info.Pid))
        {
            return new BridgeConnection(null, null, "stale bridge.json: Unity (pid " + info.Pid.ToString(CultureInfo.InvariantCulture) + ") is not running");
        }

        if (!Version.TryParse(info.Bridge, out Version? version) || version < Version.Parse(MinimumBridge))
        {
            return new BridgeConnection(null, null, "bridge " + info.Bridge + " is older than " + MinimumBridge + ": run `ug bridge install` again");
        }

        var client = new BridgeClient(projectRoot, info, isAlive);
        try
        {
            return new BridgeConnection(client, client.Status(), null);
        }
        catch (BridgeUnavailableException e)
        {
            client.Dispose();
            return new BridgeConnection(null, null, e.Message);
        }
    }

    public BridgeStatus Status() => BridgeJson.Parse<BridgeStatus>(Get("/status", RequestTimeout));

    /// <summary>Refreshes the asset database; a big import can take a while, hence the longer timeout.</summary>
    public RefreshAnswer Refresh(TimeSpan timeout) => BridgeJson.Parse<RefreshAnswer>(Get("/refresh", timeout));

    public CompileResult LastCompile() => BridgeJson.Parse<CompileResult>(Get("/compile", RequestTimeout));

    public RunOutcome Run(string target, TimeSpan timeout) =>
        BridgeJson.Parse<RunOutcome>(Get("/run?target=" + Uri.EscapeDataString(target), timeout));

    public ConsolePage Console(long after, int limit, string? types)
    {
        string query = "/console?after=" + after.ToString(CultureInfo.InvariantCulture) + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(types))
        {
            query += "&types=" + Uri.EscapeDataString(types);
        }

        return BridgeJson.Parse<ConsolePage>(Get(query, RequestTimeout));
    }

    /// <summary>Starts a test run and returns its id.</summary>
    public string StartTests(string platform, string? filter)
    {
        string query = "/tests?platform=" + Uri.EscapeDataString(platform);
        if (!string.IsNullOrEmpty(filter))
        {
            query += "&filter=" + Uri.EscapeDataString(filter);
        }

        using JsonDocument doc = JsonDocument.Parse(Get(query, RequestTimeout));
        return doc.RootElement.TryGetProperty("id", out JsonElement id) && id.GetString() is { Length: > 0 } text
            ? text
            : throw new BridgeUnavailableException("the bridge gave no test run id");
    }

    public TestRunResult TestResult(string id) =>
        BridgeJson.Parse<TestRunResult>(Get("/tests/result?id=" + Uri.EscapeDataString(id), RequestTimeout));

    /// <summary>
    /// Sends an editor-ops request (path and query, values escaped by the caller) and reads its
    /// answer: lines, a refusal, and maybe a state to wait for (<see cref="WaitFor"/>).
    /// </summary>
    public OpAnswer Op(string pathAndQuery, TimeSpan timeout)
    {
        OpAnswer answer = BridgeJson.Parse<OpAnswer>(Get(pathAndQuery, timeout));
        return answer.Lines == null && answer.Error == null
            ? throw new BridgeUnavailableException("the bridge answered neither lines nor an error")
            : answer;
    }

    /// <summary>
    /// Polls the status until the editor is in <paramref name="state"/> (`playing` counts `paused`
    /// too), riding out the domain reload play mode starts with, as the compile wait does.
    /// </summary>
    public WaitOutcome WaitFor(string state, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        string last = "unknown";
        string doing = state == "playing" ? "entering play mode" : "leaving play mode";
        while (true)
        {
            try
            {
                last = Status().State;
                if (last == state || (state == "playing" && last == "paused"))
                {
                    return new WaitOutcome(null, clock.Elapsed);
                }
            }
            catch (BridgeUnavailableException)
            {
                if (!Reconnect("while " + doing, out string? gone))
                {
                    return new WaitOutcome(gone, clock.Elapsed);
                }

                last = "reloading";
            }

            if (clock.Elapsed >= timeout)
            {
                return new WaitOutcome(
                    "Unity was still " + last + " after " + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s (" + doing + ")",
                    clock.Elapsed);
            }

            Thread.Sleep(PollInterval);
        }
    }

    /// <summary>
    /// Refreshes and waits for the compile it starts (see Compile flow in the spec). Never throws
    /// for a bridge that goes away mid-wait: the outcome says what is known.
    /// </summary>
    public CompileOutcome Compile(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        RefreshAnswer refresh;
        try
        {
            refresh = Refresh(timeout);
        }
        catch (BridgeUnavailableException e)
        {
            return CompileOutcome.Failed(e.Message, clock.Elapsed);
        }

        if (!refresh.Compiling)
        {
            return Answer(changed: false, clock);
        }

        // A reload refuses connections until the bridge is back, possibly on another port.
        while (clock.Elapsed < timeout)
        {
            Thread.Sleep(PollInterval);
            BridgeStatus status;
            try
            {
                status = Status();
            }
            catch (BridgeUnavailableException)
            {
                if (!Reconnect("during the compile", out string? gone))
                {
                    return CompileOutcome.Failed(gone!, clock.Elapsed);
                }

                continue;
            }

            if (status.Compile > refresh.Compile && status.State == "idle")
            {
                return Answer(changed: true, clock);
            }
        }

        return CompileOutcome.Failed(
            "Unity did not finish compiling within " + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s",
            clock.Elapsed,
            timedOut: true);
    }

    public void Dispose() => _http.Dispose();

    internal static BridgeInfo? ReadDiscovery(string projectRoot, out string? problem)
    {
        problem = null;
        string path = DiscoveryPath(projectRoot);
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            problem = "no bridge.json: Unity is closed or the bridge is not installed";
            return null;
        }
        catch (IOException e)
        {
            problem = "bridge.json unreadable: " + e.Message;
            return null;
        }

        try
        {
            BridgeInfo info = BridgeJson.Parse<BridgeInfo>(json);
            if (info.Port is > 0 and < 65536 && !string.IsNullOrEmpty(info.Token) && !string.IsNullOrEmpty(info.Bridge))
            {
                return info;
            }
        }
        catch (BridgeUnavailableException)
        {
        }

        problem = "bridge.json unreadable";
        return null;
    }

    /// <summary>Whether a process with this id is alive and is a Unity editor (ids get reused).</summary>
    internal static bool IsUnityRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private CompileOutcome Answer(bool changed, Stopwatch clock)
    {
        try
        {
            return new CompileOutcome(changed, LastCompile(), null, false, clock.Elapsed);
        }
        catch (BridgeUnavailableException e)
        {
            return CompileOutcome.Failed(e.Message, clock.Elapsed);
        }
    }

    // After a refused request: still waiting while the editor lives; picks up a new port or token.
    // during: when it happened, for the message if the editor is gone ("during the compile").
    private bool Reconnect(string during, out string? problem)
    {
        problem = null;
        if (!_isAlive(_info.Pid))
        {
            problem = "Unity (pid " + _info.Pid.ToString(CultureInfo.InvariantCulture) + ") exited " + during;
            return false;
        }

        if (ReadDiscovery(_projectRoot, out _) is { } info && info.Pid == _info.Pid)
        {
            _info = info;
        }

        return true;
    }

    private string Get(string pathAndQuery, TimeSpan timeout)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:" + _info.Port.ToString(CultureInfo.InvariantCulture) + pathAndQuery);
        request.Headers.Add("X-UG-Token", _info.Token);
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            using HttpResponseMessage response = _http.Send(request, cancel.Token);
            using var reader = new StreamReader(response.Content.ReadAsStream(cancel.Token));
            string body = reader.ReadToEnd();
            return response.StatusCode == HttpStatusCode.OK ? body : throw new BridgeUnavailableException(Failure(response.StatusCode, body));
        }
        catch (HttpRequestException e)
        {
            throw new BridgeUnavailableException(e.HttpRequestError == HttpRequestError.ConnectionError ? Refused : "request failed: " + e.Message);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new BridgeUnavailableException(Refused); // the connect timeout
        }
        catch (OperationCanceledException)
        {
            throw new BridgeUnavailableException("no answer within " + timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s: Unity is busy");
        }
        catch (IOException e)
        {
            throw new BridgeUnavailableException("connection lost: " + e.Message);
        }
    }

    private const string Refused = "refused: Unity is starting or reloading scripts";

    private static string Failure(HttpStatusCode status, string body)
    {
        string? error = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out JsonElement e))
            {
                error = e.GetString();
            }
        }
        catch (JsonException)
        {
        }

        string code = ((int)status).ToString(CultureInfo.InvariantCulture);
        return status == HttpStatusCode.Unauthorized
            ? "the bridge refused the token (" + code + "): bridge.json is out of date"
            : "bridge answered " + code + (error == null ? "" : ": " + error);
    }
}

/// <summary>
/// What a compile wait ended with: the compile's messages, or the problem that ended the wait.
/// <see cref="Changed"/> is false when the refresh started no compile.
/// </summary>
public sealed record CompileOutcome(bool Changed, CompileResult? Result, string? Problem, bool TimedOut, TimeSpan Elapsed)
{
    internal static CompileOutcome Failed(string problem, TimeSpan elapsed, bool timedOut = false) => new(false, null, problem, timedOut, elapsed);
}
