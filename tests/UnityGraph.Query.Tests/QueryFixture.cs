using Microsoft.Data.Sqlite;
using UnityGraph.Assets;
using UnityGraph.Code;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Query.Tests;

/// <summary>`fixtures/CodeProject` copied to a temp folder and indexed once for the whole class.</summary>
public sealed class QueryFixture : IDisposable
{
    public QueryFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "ug-query-" + Guid.NewGuid().ToString("N")[..8]);
        Copy(Path.Combine(RepoRoot(), "fixtures", "CodeProject"), Root);
        using GraphStore store = GraphStore.Open(Root, StoreMode.ReadWrite);
        CodeIndex.Update(CodeWorkspace.Load(Root, VendoredFolders.Default), store);
        AssetIndex.Update(AssetWorkspace.Scan(Root, VendoredFolders.Default), store, []);
    }

    public string Root { get; }

    public GraphStore Open() => GraphStore.Open(Root, StoreMode.ReadOnly);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is harmless.
        }
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
