using System.Globalization;
using System.Text;
using UnityGraph.EditorLink.Tests;

namespace UnityGraph.Cli.Tests;

/// <summary>A bare Unity project folder and a fake bridge for the unity-loop command tests.</summary>
public abstract class LoopFixture : IDisposable
{
    protected LoopFixture()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Assets"));
        Directory.CreateDirectory(Path.Combine(Root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(Root, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.3.14f1\n");
        EditorCommands.ProcessCheck = _ => true;
    }

    internal FakeBridge Bridge { get; } = new();

    protected string Root { get; } = Path.Combine(Path.GetTempPath(), "ug-loop-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        EditorCommands.ProcessCheck = null;
        Bridge.Dispose();
        Directory.Delete(Root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>A console answer: entries as (id, type, text, frame, count).</summary>
    protected static string ConsoleJson(long last, params (long Id, string Type, string Text, string? Frame, int Count)[] entries)
    {
        var text = new StringBuilder("{\"last\":").Append(last.ToString(CultureInfo.InvariantCulture)).Append(",\"entries\":[");
        text.AppendJoin(',', entries.Select(e =>
            "{\"id\":" + e.Id.ToString(CultureInfo.InvariantCulture) + ",\"type\":\"" + e.Type + "\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(e.Text)
            + ",\"frame\":" + (e.Frame == null ? "null" : "\"" + e.Frame + "\"") + ",\"count\":" + e.Count.ToString(CultureInfo.InvariantCulture) + "}"));
        return text.Append("]}").ToString();
    }

    /// <summary>A bridge 0.9.0 console answer: entries as (id, type, text, frame, count, stack), and the oldest id held.</summary>
    protected static string FullConsoleJson(long last, long oldest, params (long Id, string Type, string Text, string? Frame, int Count, string? Stack)[] entries)
    {
        var text = new StringBuilder("{\"last\":").Append(last.ToString(CultureInfo.InvariantCulture))
            .Append(",\"oldest\":").Append(oldest.ToString(CultureInfo.InvariantCulture)).Append(",\"entries\":[");
        text.AppendJoin(',', entries.Select(e =>
            "{\"id\":" + e.Id.ToString(CultureInfo.InvariantCulture) + ",\"type\":\"" + e.Type + "\",\"text\":" + System.Text.Json.JsonSerializer.Serialize(e.Text)
            + ",\"frame\":" + (e.Frame == null ? "null" : "\"" + e.Frame + "\"") + ",\"stack\":" + (e.Stack == null ? "null" : System.Text.Json.JsonSerializer.Serialize(e.Stack))
            + ",\"count\":" + e.Count.ToString(CultureInfo.InvariantCulture) + "}"));
        return text.Append("]}").ToString();
    }

    protected (int ExitCode, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int exitCode = CliApp.Run([.. args, "--project", Root], output, error, editorLog: Path.Combine(Root, "no-editor.log"));
        Assert.Equal("", error.ToString());
        return (exitCode, output.ToString().ReplaceLineEndings("\n"));
    }
}
