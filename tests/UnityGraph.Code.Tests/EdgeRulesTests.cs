using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Code.Tests;

/// <summary>Edge rules the fixture's golden graph doesn't reach, each on a small extra file.</summary>
public sealed class EdgeRulesTests : IDisposable
{
    private const string Path = "Assets/Scripts/Lab/Extra.cs";

    private readonly CodeFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Extension_methods_indexers_operators_and_method_groups_are_calls()
    {
        IReadOnlyList<string> edges = Edges("""
            using System;
            namespace Game.Lab
            {
                public static class Ext { public static int Twice(this int x) => x * 2; }
                public class Grid
                {
                    public int this[int i] => i;
                    public static Grid operator -(Grid g) => g;
                    public static explicit operator int(Grid g) => 0;
                    public int Size { get; set; }
                    public void Tick() { }
                }
                public class User
                {
                    public void Run(Grid g)
                    {
                        int a = 3.Twice() + g[1] + (int)(-g);
                        g.Size += a;
                        Action tick = g.Tick;
                    }
                }
            }
            """);

        Assert.Contains("cs:M:Game.Lab.User.Run(Game.Lab.Grid) calls cs:M:Game.Lab.Ext.Twice(System.Int32) @17", edges);
        Assert.Contains("cs:M:Game.Lab.User.Run(Game.Lab.Grid) calls cs:P:Game.Lab.Grid.Item(System.Int32) @17", edges);
        Assert.Contains("cs:M:Game.Lab.User.Run(Game.Lab.Grid) calls cs:M:Game.Lab.Grid.op_UnaryNegation(Game.Lab.Grid) @17", edges);
        Assert.Contains("cs:M:Game.Lab.User.Run(Game.Lab.Grid) calls cs:M:Game.Lab.Grid.op_Explicit(Game.Lab.Grid)~System.Int32 @17", edges);
        Assert.Contains("cs:M:Game.Lab.User.Run(Game.Lab.Grid) calls cs:P:Game.Lab.Grid.Size @18", edges);
        Assert.Contains("cs:M:Game.Lab.User.Run(Game.Lab.Grid) calls cs:M:Game.Lab.Grid.Tick @19", edges);
        Assert.Contains("cs:M:Game.Lab.User.Run(Game.Lab.Grid) uses_type cs:T:Game.Lab.Grid @15", edges);
    }

    [Fact]
    public void Interface_extending_interface_is_inherits()
    {
        IReadOnlyList<string> edges = Edges("""
            namespace Game.Lab
            {
                public interface IA { }
                public interface IB : IA { }
                public class Both : IB, System.IDisposable { public void Dispose() { } }
            }
            """);

        Assert.Contains("cs:T:Game.Lab.IB inherits cs:T:Game.Lab.IA @4", edges);
        Assert.Contains("cs:T:Game.Lab.Both implements cs:T:Game.Lab.IB @5", edges);
        Assert.Contains("cs:T:Game.Lab.Both implements cs:T:System.IDisposable @5", edges);
        Assert.Contains("cs:M:Game.Lab.Both.Dispose overrides cs:M:System.IDisposable.Dispose @5", edges);
    }

    [Fact]
    public void Invoke_on_another_object_searches_that_objects_type()
    {
        IReadOnlyList<string> edges = Edges("""
            using UnityEngine;
            namespace Game.Lab
            {
                public class Caller : MonoBehaviour
                {
                    public Turntable table;
                    void Go()
                    {
                        table.Invoke("Stop", 1f);
                        table.StartCoroutine("Missing");
                        Invoke(nameof(Go), 2f);
                        string name = "Stop";
                        table.Invoke(name, 1f);
                    }
                }
            }
            """);

        Assert.Contains("cs:M:Game.Lab.Caller.Go dispatches cs:M:Game.Lab.Turntable.Stop exact @9", edges);
        Assert.Contains("cs:M:Game.Lab.Caller.Go dispatches cs:M:Game.Lab.Caller.Go exact @11", edges);
        Assert.DoesNotContain(edges, e => e.Contains("Missing", StringComparison.Ordinal));
        Assert.Single(edges, e => e.Contains("dispatches", StringComparison.Ordinal) && e.Contains("Stop", StringComparison.Ordinal));
    }

    [Fact]
    public void Send_message_targets_are_all_kept()
    {
        var types = string.Join("\n", Enumerable.Range(0, 25).Select(i => $"public class M{i:00} : MonoBehaviour {{ void Ping() {{ }} }}"));
        List<Edge> edges = Index($$"""
            using UnityEngine;
            namespace Game.Lab
            {
            {{types}}
                public class Sender : MonoBehaviour { void Go() => BroadcastMessage("Ping"); }
            }
            """).Edges.Where(e => e.Kind == EdgeKind.Dispatches).ToList();

        Assert.Equal(25, edges.Count);
        Assert.All(edges, e => Assert.Equal(Confidence.Heuristic, e.Confidence));
        Assert.All(edges, e => Assert.Null(e.Props));
        Assert.Equal("cs:M:Game.Lab.M00.Ping", edges[0].Dst);
    }

    [Fact]
    public void Long_signatures_and_type_names_are_stored_whole()
    {
        string parameters = string.Join(", ", Enumerable.Range(0, 12).Select(i => $"System.Collections.Generic.Dictionary<string, int> map{i}"));
        string type = "System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<int, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Collections.Generic.HashSet<string>>>>>>";
        FileContribution file = Index($$"""
            namespace Game.Lab
            {
                public class Wide
                {
                    public void Many({{parameters}}) { }
                    public {{type}} table;
                }
            }
            """);

        string sig = "Many(" + string.Join(", ", Enumerable.Repeat("Dictionary<string, int>", 12)) + ")";
        Assert.Contains("\"sig\":\"" + sig + "\"", file.Nodes.Single(n => n.Name == "Many").Props, StringComparison.Ordinal);
        const string Shown = "Dictionary<string, List<KeyValuePair<int, Dictionary<string, List<HashSet<string>>>>>>";
        Assert.Contains("\"type\":\"" + Shown + "\"", file.Nodes.Single(n => n.Name == "table").Props, StringComparison.Ordinal);
        Assert.True(Shown.Length > 80);
        Assert.True(sig.Length > 120);
    }

    [Fact]
    public void Vendored_files_get_declarations_only()
    {
        _fixture.Write(Path, "namespace Game.Lab { public class Extra : Turntable { void Go() { Stop(); } } }");

        FileContribution file = _fixture.Load(new VendoredFolders(["Assets/Scripts/Lab/"])).Index(Path);

        Assert.Equal(["contains", "inherits"], file.Edges.Select(e => KindNames.Of(e.Kind)).Distinct().Order());
    }

    [Fact]
    public void Explicit_implementations_and_generic_methods_are_named_plainly()
    {
        FileContribution file = Index("""
            using System;
            namespace Game.Lab
            {
                public class Res : IDisposable
                {
                    void IDisposable.Dispose() { }
                    public T Make<T>(ref int count, params string[] names) where T : new() => new T();
                }
            }
            """);

        Node dispose = file.Nodes.Single(n => n.Key.Contains("Dispose", StringComparison.Ordinal));
        Node make = file.Nodes.Single(n => n.Name == "Make");
        Assert.Equal("Dispose", dispose.Name);
        Assert.Contains("\"sig\":\"Make<T>(ref int, params string[])\"", make.Props, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreadable_file_is_an_error_record()
    {
        _fixture.Write(Path, "namespace Game.Lab { }");
        CodeWorkspace workspace = _fixture.Load();
        using (File.Open(System.IO.Path.Combine(_fixture.Root, Path), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            FileContribution file = workspace.Index(Path);

            Assert.Equal(FileStatus.Error, file.File.Status);
            Assert.Empty(file.Nodes);
        }
    }

    private FileContribution Index(string source)
    {
        _fixture.Write(Path, source);
        return _fixture.Load().Index(Path);
    }

    private IReadOnlyList<string> Edges(string source) =>
        Index(source).Edges
            .Select(e => $"{e.Src} {KindNames.Of(e.Kind)} {e.Dst}{(e.Kind == EdgeKind.Dispatches ? " " + e.Confidence.ToString().ToLowerInvariant() : "")} @{e.Line}")
            .ToList();
}
