using System.Text;
using UnityGraph.Core;

namespace UnityGraph.Hooks;

/// <summary>
/// Runs the handlers for one hook call and fails open (design rule 9): any exception or a run
/// over the time budget gives exit 0 and no output, plus one line in
/// `Library/unitygraph/hook-errors.log` when the Unity project is known.
/// </summary>
public sealed class HookDispatcher
{
    public const string ErrorLogPath = "Library/unitygraph/hook-errors.log";

    /// <summary>The log moves to hook-errors.log.1 past this size, so a failing handler can't fill the disk.</summary>
    public const long MaxErrorLogBytes = 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<IHookHandler> _handlers;
    private readonly TimeSpan _timeout;
    private readonly Func<DateTimeOffset> _clock;

    public HookDispatcher(IReadOnlyList<IHookHandler> handlers, TimeSpan? timeout = null, Func<DateTimeOffset>? clock = null)
    {
        _handlers = handlers;
        _timeout = timeout ?? DefaultTimeout;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Always returns 0.</summary>
    // fallbackCwd: where to look for the Unity project when the payload has no `cwd`.
    public int Run(string eventName, TextReader stdin, TextWriter stdout, string fallbackCwd)
    {
        UnityProject? project = null;
        try
        {
            using var cancel = new CancellationTokenSource();
            Task<string> work = Task.Run(() => Dispatch(eventName, stdin, fallbackCwd, p => project = p, cancel.Token));
            if (!work.Wait(_timeout))
            {
                cancel.Cancel();
                Log(project, eventName, $"timed out after {_timeout.TotalSeconds:0.#} s");
                return 0;
            }

            stdout.Write(work.Result);
        }
        catch (Exception e)
        {
            Log(project, eventName, Describe(e is AggregateException { InnerException: { } inner } ? inner : e));
        }

        return 0;
    }

    private string Dispatch(string eventName, TextReader stdin, string fallbackCwd, Action<UnityProject> found, CancellationToken cancel)
    {
        string json = stdin.ReadToEnd();
        HookInput input;
        try
        {
            input = HookInput.Parse(eventName, json);
        }
        catch (FormatException)
        {
            // Still find the project, so the bad payload is logged.
            if (UnityProject.Find(fallbackCwd) is { } fallback)
            {
                found(fallback);
            }

            throw;
        }

        UnityProject? project = UnityProject.Find(input.Cwd ?? fallbackCwd);
        if (project is null)
        {
            return "";
        }

        found(project);
        List<IHookHandler> handlers = _handlers.Where(h => h.Handles(input)).ToList();
        if (handlers.Count == 0)
        {
            return "";
        }

        UnityGraphConfig config = UnityGraphConfig.Load(project.Root);
        var output = new StringBuilder();
        foreach (IHookHandler handler in handlers.Where(h => config.IsEnabled(h.Module)))
        {
            string? text;
            try
            {
                text = handler.Handle(input, project, cancel);
            }
            catch (Exception e)
            {
                throw new HookHandlerException(handler.Module, e);
            }

            if (!string.IsNullOrEmpty(text))
            {
                output.Append(output.Length == 0 ? "" : "\n").Append(text);
            }
        }

        return output.ToString();
    }

    private static string Describe(Exception e) => e switch
    {
        HookHandlerException h => $"{h.Module}: {h.InnerException!.GetType().Name}: {h.InnerException.Message}",
        FormatException => e.Message,
        _ => $"{e.GetType().Name}: {e.Message}",
    };

    private void Log(UnityProject? project, string eventName, string message)
    {
        if (project is null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(project.Root, ErrorLogPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaxErrorLogBytes)
            {
                File.Move(path, path + ".1", overwrite: true);
            }

            string line = $"{_clock():yyyy-MM-ddTHH:mm:ssZ} {eventName} {message.ReplaceLineEndings(" ")}\n";
            File.AppendAllText(path, line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report; the session carries on.
        }
    }

    /// <summary>A handler threw; carries the module name into the log line.</summary>
    private sealed class HookHandlerException(string module, Exception inner) : Exception(inner.Message, inner)
    {
        public string Module { get; } = module;
    }
}
