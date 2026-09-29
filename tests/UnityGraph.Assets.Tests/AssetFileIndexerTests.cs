using System.Text;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Assets.Tests;

public sealed class AssetFileIndexerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-asset-" + Guid.NewGuid().ToString("N")[..8]);

    public static string Source { get; } = Path.Combine(RepoRoot(), "fixtures", "CodeProject");

    [Fact]
    public void Fixture_assets_index_to_the_golden_rows()
    {
        var problems = new List<string>();
        var files = new List<FileContribution>();
        foreach (string path in Files(Source))
        {
            if (path.EndsWith(".meta", StringComparison.Ordinal))
            {
                files.Add(AssetFileIndexer.Meta(Source, path, problems));
            }
            else if (AssetTypes.IsContent(path) && !VendoredFolders.Default.Contains(path))
            {
                files.Add(AssetFileIndexer.Content(Source, path, problems));
            }
        }

        Golden.Match(Render(files) + "# problems\n" + string.Concat(problems.Select(p => "  " + p + "\n")));
    }

    [Fact]
    public void Folder_meta_gives_no_rows()
    {
        var problems = new List<string>();
        FileContribution file = AssetFileIndexer.Meta(Source, "Assets/Prefabs.meta", problems);
        Assert.Equal(FileStatus.Ok, file.File.Status);
        Assert.Empty(file.Nodes);
        Assert.Empty(problems);
    }

    [Fact]
    public void Meta_without_guid_is_an_error()
    {
        Write("Assets/Broken.asset.meta", "fileFormatVersion: 2\nNativeFormatImporter:\n  mainObjectFileID: 11400000\n");
        var problems = new List<string>();
        FileContribution file = AssetFileIndexer.Meta(_root, "Assets/Broken.asset.meta", problems);
        Assert.Equal(FileStatus.Error, file.File.Status);
        Assert.Equal(["Assets/Broken.asset.meta: no guid"], problems);
    }

    [Fact]
    public void Missing_file_is_an_error()
    {
        Directory.CreateDirectory(_root);
        var problems = new List<string>();
        FileContribution file = AssetFileIndexer.Content(_root, "Assets/Gone.prefab", problems);
        Assert.Equal(FileStatus.Error, file.File.Status);
        Assert.Single(problems);
    }

    [Fact]
    public void Duplicate_file_ids_keep_the_first_object()
    {
        Write("Assets/Twice.prefab.meta", "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdef\nPrefabImporter:\n");
        Write("Assets/Twice.prefab", """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1 &1
            GameObject:
              m_Name: First
              m_IsActive: 1
            --- !u!1 &1
            GameObject:
              m_Name: Second
              m_IsActive: 1

            """);
        var problems = new List<string>();
        FileContribution file = AssetFileIndexer.Content(_root, "Assets/Twice.prefab", problems);
        Assert.Contains(file.Nodes, n => n.Name == "First");
        Assert.DoesNotContain(file.Nodes, n => n.Name == "Second");
        Assert.Single(problems);
        Assert.Contains("fileID 1 appears twice", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_yaml_keeps_what_came_before()
    {
        Write("Assets/Odd.asset.meta", "fileFormatVersion: 2\nguid: 0123456789abcdef0123456789abcdee\nNativeFormatImporter:\n  mainObjectFileID: 11400000\n");
        Write("Assets/Odd.asset", """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!114 &11400000
            MonoBehaviour:
              m_Script: {fileID: 11500000, guid: a7000000000000000000000000000007, type: 3}
              m_Name: Odd
              target: {fileID: 100, guid: b1000000000000000000000000000001, type: 3}
              note: |
                block
              later: {fileID: 102900000, guid: c3000000000000000000000000000003, type: 3}

            """);
        var problems = new List<string>();
        FileContribution file = AssetFileIndexer.Content(_root, "Assets/Odd.asset", problems);
        Assert.Contains(file.Edges, e => e.Dst == "a:b1000000000000000000000000000001");
        Assert.DoesNotContain(file.Edges, e => e.Dst == "a:c3000000000000000000000000000003");
        Assert.Single(problems);
    }

    [Fact]
    public void Indexing_is_deterministic()
    {
        var problems = new List<string>();
        string first = Render([AssetFileIndexer.Content(Source, "Assets/Scenes/Lab.unity", problems)]);
        string second = Render([AssetFileIndexer.Content(Source, "Assets/Scenes/Lab.unity", problems)]);
        Assert.Equal(first, second);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
    }

    private void Write(string path, string text)
    {
        string full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"));
    }

    private static IEnumerable<string> Files(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    /// <summary>One line per node, then per edge, grouped by file.</summary>
    private static string Render(IEnumerable<FileContribution> files)
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
