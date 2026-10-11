using System.Diagnostics;
using System.Text;

namespace Ungrep.Bench;

/// <summary>What one call returned: its text as Claude would see it, the exit code, and how long it took.</summary>
public sealed record CallAnswer(string Text, int Exit, TimeSpan Elapsed);

/// <summary>An open MCP server: tool calls, and its tool list for the idle cost.</summary>
public interface IMcpSession : IDisposable
{
    CallAnswer Call(string tool, string argumentsJson, TimeSpan timeout);

    /// <summary>Every tool's name, description and input schema as `tools/list` gave them, one JSON object each.</summary>
    IReadOnlyList<(string Name, string Json)> Tools();
}

/// <summary>
/// The outside world the runner works through: a shell, files, MCP servers and processes. Tests
/// replace it, so the runner is checked without shells, editors or `claude`.
/// </summary>
public interface IBenchHost
{
    CallAnswer Shell(string command, string cwd, TimeSpan timeout);

    /// <summary>The file's text; null when it does not exist.</summary>
    string? ReadFile(string path);

    IMcpSession OpenMcp(McpServer server, string cwd);

    /// <summary>Runs a program with arguments (no shell); stdout and stderr apart.</summary>
    (int Exit, string Stdout, string Stderr, TimeSpan Elapsed) Exec(string program, IReadOnlyList<string> args, string cwd, TimeSpan timeout, string? stdin = null);
}

/// <summary>
/// The real host. Shell commands run with `bash -c` (Git Bash on Windows, as Claude Code's hooks
/// do; never WSL's `System32\bash.exe`), stderr folded into stdout in order, the way the Bash tool
/// shows a command's output. The `bin/` beside the running `ug` goes first on PATH so `ug` in a
/// step is this build.
/// </summary>
public sealed class BenchHost : IBenchHost
{
    private readonly string _bash;
    private readonly string? _extraPath;

    public BenchHost(string? extraPath = null)
    {
        _bash = FindBash();
        _extraPath = extraPath;
    }

    public CallAnswer Shell(string command, string cwd, TimeSpan timeout)
    {
        // `exec 2>&1` keeps stdout and stderr interleaved as written, without changing the command.
        var (exit, stdout, _, elapsed) = Exec(_bash, ["-c", "exec 2>&1\n" + command], cwd, timeout);
        return new CallAnswer(stdout, exit, elapsed);
    }

    public string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public IMcpSession OpenMcp(McpServer server, string cwd)
    {
        string command = "exec " + string.Join(' ', new[] { server.Command }.Concat(server.Args).Select(Quote));
        ProcessStartInfo start = StartInfo(_bash, ["-c", command], cwd);
        start.RedirectStandardInput = true;
        var process = Process.Start(start) ?? throw new IOException("could not start " + server.Command);
        return new McpClient(process);
    }

    public (int Exit, string Stdout, string Stderr, TimeSpan Elapsed) Exec(string program, IReadOnlyList<string> args, string cwd, TimeSpan timeout, string? stdin = null)
    {
        ProcessStartInfo start = StartInfo(program, args, cwd);
        start.RedirectStandardInput = stdin is not null;
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new IOException("could not start " + program);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(stderr, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Ended between the wait and the kill.
            }

            process.WaitForExit();
            Append(stdout, $"Command timed out after {timeout.TotalSeconds:0} s");
            return (-1, stdout.ToString(), stderr.ToString(), watch.Elapsed);
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.ToString(), stderr.ToString(), watch.Elapsed);
    }

    private ProcessStartInfo StartInfo(string program, IReadOnlyList<string> args, string cwd)
    {
        var start = new ProcessStartInfo(program)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        if (_extraPath is not null)
        {
            start.Environment["PATH"] = _extraPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }

        return start;
    }

    private static void Append(StringBuilder text, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (text)
        {
            text.Append(line).Append('\n');
        }
    }

    private static string Quote(string arg) => "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>UG_BENCH_SHELL, then Git for Windows' bash, then `bash` on PATH.</summary>
    private static string FindBash()
    {
        if (Environment.GetEnvironmentVariable("UG_BENCH_SHELL") is { Length: > 0 } configured)
        {
            return configured;
        }

        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/bin/bash") ? "/bin/bash" : "bash";
        }

        foreach (string? root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetEnvironmentVariable("LOCALAPPDATA") + "\\Programs" })
        {
            if (root is not null && Path.Combine(root, "Git", "bin", "bash.exe") is var bash && File.Exists(bash))
            {
                return bash;
            }
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            // git.exe sits in <Git>\cmd; its bash in <Git>\bin.
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, "git.exe")) && Path.Combine(dir, "..", "bin", "bash.exe") is var bash && File.Exists(bash))
            {
                return Path.GetFullPath(bash);
            }
        }

        return "bash";
    }
}
