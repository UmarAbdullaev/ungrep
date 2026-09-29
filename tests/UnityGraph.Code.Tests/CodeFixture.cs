using System.Text;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Code.Tests;

/// <summary>A temp copy of `fixtures/CodeProject`, deleted on dispose, and text renderings of what it indexes to.</summary>
internal sealed class CodeFixture : IDisposable
{
    public CodeFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "ug-code-" + Guid.NewGuid().ToString("N")[..8]);
        Copy(Source, Root);
    }

    public string Root { get; }

    public static string Source { get; } = Path.Combine(RepoRoot(), "fixtures", "CodeProject");

    public CodeWorkspace Load(VendoredFolders? vendored = null) => CodeWorkspace.Load(Root, vendored ?? VendoredFolders.Default);

    public void Write(string path, string text)
    {
        string full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    /// <summary>One line per node, then per edge, grouped by file: stable, diffable golden output.</summary>
    public static string Render(IEnumerable<FileContribution> files)
    {
        var text = new StringBuilder();
        foreach (FileContribution file in files)
        {
            text.Append("# ").Append(file.File.Path).Append(file.File.Status == FileStatus.Ok ? "" : " " + file.File.Status).Append('\n');
            foreach (Node node in file.Nodes)
            {
                text.Append($"  {KindNames.Of(node.Kind)} {node.Key} \"{node.Name}\" {node.LineStart}-{node.LineEnd}");
                text.Append(node.Props is null ? "" : " " + node.Props).Append('\n');
            }

            foreach (Edge edge in file.Edges)
            {
                text.Append($"  {edge.Src} {KindNames.Of(edge.Kind)} {edge.Dst} {edge.Confidence.ToString().ToLowerInvariant()} @{edge.Line}");
                text.Append(edge.Props is null ? "" : " " + edge.Props).Append('\n');
            }
        }

        return text.ToString();
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            Copy(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "unitygraph.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("unitygraph.sln not found above " + AppContext.BaseDirectory);
    }
}
