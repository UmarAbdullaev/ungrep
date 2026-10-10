using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UnityGraph.Assets;
using UnityGraph.Code;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Hooks.Tests;

/// <summary>
/// SPEC-guards replay: every recorded `Edit`, `MultiEdit`, `Write`, `Bash` and `PowerShell` call of
/// the purge-squad sessions goes through the guards against today's project. Runs when
/// `UG_PURGE_SQUAD` names the project; transcripts from `UG_PURGE_SQUAD_TRANSCRIPTS` or
/// `~/.claude/projects/D--Projects-Unity-purge-squad`. Reads the project only: the graph is kept in
/// a temp folder and the once-per-session filter is kept in memory. Fails on any deny; prints
/// every note for the spec's Measured section.
/// </summary>
public sealed class PurgeSquadReplayTests : IDisposable
{
    private readonly string _graph = Path.Combine(Path.GetTempPath(), "ug-ps-guards-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Dictionary<(string, DateTimeOffset), string> _before = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_graph))
        {
            Directory.Delete(_graph, recursive: true);
        }
    }

    [Fact]
    public void Recorded_calls_get_no_deny_and_every_note_is_printed()
    {
        string? root = Environment.GetEnvironmentVariable("UG_PURGE_SQUAD");
        if (root is null)
        {
            Assert.Skip("UG_PURGE_SQUAD not set");
        }

        string transcripts = Environment.GetEnvironmentVariable("UG_PURGE_SQUAD_TRANSCRIPTS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", "D--Projects-Unity-purge-squad");
        UnityGraphConfig config = UnityGraphConfig.Load(root);
        using (GraphStore store = GraphStore.Open(root, StoreMode.ReadWrite, graphDirectory: _graph))
        {
            CodeIndex.Update(CodeWorkspace.Load(root, config.Vendored), store);
            AssetIndex.Update(AssetWorkspace.Scan(root, config.Vendored), store, []);
        }

        UnityProject project = UnityProject.Find(root)!;
        var output = TestContext.Current.TestOutputHelper!;
        var fired = new HashSet<string>(StringComparer.Ordinal);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var denies = new List<string>();
        int calls = 0, goneOld = 0, olderWrites = 0, shellOps = 0, notes = 0;
        var times = new List<double>();
        foreach ((HookInput input, DateTimeOffset time) in Calls(transcripts))
        {
            calls++;
            using var context = new GuardContext(input, project, config, _graph);
            EditCall? edit = EditCall.From(input);
            string? path = edit is null ? null : context.ProjectPath(edit.FilePath);
            if (edit is { Edits.Count: > 0 } && path is not null
                && (!File.Exists(context.FullPath(path)) || edit.Apply(EditCall.Lf(File.ReadAllText(context.FullPath(path)))) is null))
            {
                goneOld++;
                continue;
            }

            foreach (ShellOp op in context.ShellOps().Where(o => o.Sources.Any(GuardContext.InAssets)))
            {
                shellOps++;
                output.WriteLine($"[{input.SessionId?[..8]} {time:yyyy-MM-dd HH:mm} {input.ToolName}] {op.Verb} {string.Join(" ", op.Sources.Select(s => s ?? "(outside)"))}"
                    + (op.Kind == FileOpKind.Remove ? "" : " -> " + (op.Destination ?? "(outside)")));
            }

            var clock = Stopwatch.StartNew();
            List<(GuardNote Note, GuardMode Mode)> found = GuardsHandler.Check(context, CancellationToken.None);
            times.Add(clock.Elapsed.TotalMilliseconds);
            foreach ((GuardNote note, GuardMode mode) in found)
            {
                if (mode == GuardMode.Deny)
                {
                    denies.Add(note.Text);
                }

                // A Write is compared with today's file: a field added after it looks dropped. It counts
                // only when a field it leaves out was in the file at the last commit before the call.
                if (note.Case == "field-removed" && edit?.Content is { } content && path is not null
                    && !context.Graph!.SerializedMembers(path).Any(m => !EditCall.Identifier(m.Name).IsMatch(content) && EditCall.Identifier(m.Name).IsMatch(Before(root, path, time))))
                {
                    olderWrites++;
                    continue;
                }

                if (!fired.Add(input.SessionId + " " + note.Case + " " + note.Subject))
                {
                    continue;
                }

                notes++;
                counts[note.Case] = counts.GetValueOrDefault(note.Case) + 1;
                output.WriteLine($"[{input.SessionId?[..8]} {time:yyyy-MM-dd HH:mm} {input.ToolName}]\n{note.Text}\n");
            }
        }

        output.WriteLine($"{calls} calls ({shellOps} shell moves, copies or removes under Assets/ or Packages/); not replayable: {goneOld} edits whose "
            + $"old_string is gone from today's file, {olderWrites} Write notes about fields added after the call; {notes} notes: "
            + string.Join(", ", counts.Select(c => $"{c.Key} {c.Value}")));
        times.Sort();
        output.WriteLine($"guards per call (graph open included): median {times[times.Count / 2]:0.0} ms, p99 {times[times.Count * 99 / 100]:0.0} ms, max {times[^1]:0.0} ms");
        Assert.True(calls > 0, "no recorded calls under " + transcripts);
        Assert.Empty(denies);
    }

    // The file at the project's last commit before the time; empty when it was not committed then.
    private string Before(string root, string path, DateTimeOffset time)
    {
        if (!_before.TryGetValue((path, time), out string? text))
        {
            string commit = Git(root, "rev-list", "-1", "--before=" + time.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss +0000", CultureInfo.InvariantCulture), "HEAD").Trim();
            text = _before[(path, time)] = commit.Length == 0 ? "" : Git(root, "show", commit + ":" + path);
        }

        return text;
    }

    private static string Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(root);
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var git = Process.Start(start)!;
        string text = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        return git.ExitCode == 0 ? EditCall.Lf(text) : "";
    }

    // The guarded tool calls of every transcript (subagents' too), as PreToolUse inputs with the session's id and cwd, and their time.
    private static IEnumerable<(HookInput Input, DateTimeOffset Time)> Calls(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            foreach (string line in File.ReadLines(file))
            {
                if (!line.Contains("\"tool_use\"", StringComparison.Ordinal))
                {
                    continue;
                }

                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement entry = doc.RootElement;
                if (!entry.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out JsonElement type) && type.GetString() == "tool_use"
                        && block.GetProperty("name").GetString() is "Edit" or "MultiEdit" or "Write" or "Bash" or "PowerShell" && block.TryGetProperty("input", out JsonElement input))
                    {
                        string payload = JsonSerializer.Serialize(new Dictionary<string, object?>
                        {
                            ["session_id"] = Text(entry, "sessionId"),
                            ["cwd"] = Text(entry, "cwd"),
                            ["hook_event_name"] = "PreToolUse",
                            ["tool_name"] = block.GetProperty("name").GetString(),
                            ["tool_input"] = input,
                        });
                        DateTimeOffset time = Text(entry, "timestamp") is { } stamp ? DateTimeOffset.Parse(stamp, CultureInfo.InvariantCulture) : DateTimeOffset.MinValue;
                        yield return (HookInput.Parse("PreToolUse", payload), time);
                    }
                }
            }
        }
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
