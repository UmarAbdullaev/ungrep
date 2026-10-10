using Microsoft.Data.Sqlite;
using UnityGraph.Assets;
using UnityGraph.Code;
using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Hooks.Tests;

/// <summary>
/// `fixtures/CodeProject` copied to a temp folder, with what the guards need on top: a
/// `[SerializeReference]` field (`Spinner._behaviour`) and the prefab holding it, the types it may
/// store in their own `.asmdef` (`Game.Core`), and a component no asset holds (`Unheld`). Indexed
/// once for the whole class.
/// </summary>
public sealed class GuardFixture : IDisposable
{
    public const string SpinnerGuid = "c0000000000000000000000000000003";

    public GuardFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "ug-guards-" + Guid.NewGuid().ToString("N")[..8]);
        Copy(Path.Combine(RepoRoot(), "fixtures", "CodeProject"), Root);

        Write("Assets/Scripts/Core/Behaviours.cs", """
            using System;

            namespace Game.Core
            {
                public interface IStageBehaviour
                {
                    void Tick();
                }

                [Serializable]
                public class SpinBehaviour : IStageBehaviour
                {
                    public float rate;

                    public void Tick() { }
                }
            }

            """);
        Write("Assets/Scripts/Core/Behaviours.cs.meta", Meta("c0000000000000000000000000000001", "MonoImporter"));
        Write("Assets/Scripts/Core/Game.Core.asmdef", """{ "name": "Game.Core" }""" + "\n");
        Write("Assets/Scripts/Core/Game.Core.asmdef.meta", Meta("c0000000000000000000000000000002", "AssemblyDefinitionImporter"));
        Directory.CreateDirectory(Path.Combine(Root, "Assets", "Scripts", "Extra"));
        Write("Assets/Scripts/Extra/Extra.asmref", """{ "reference": "GUID:c0000000000000000000000000000002" }""" + "\n");
        Directory.CreateDirectory(Path.Combine(Root, "Assets", "Scripts", "Named"));
        Write("Assets/Scripts/Named/Named.asmref", """{ "reference": "Game.Core" }""" + "\n");
        Write("Assets/Scripts/Lab/Spinner.cs", """
            using Game.Core;
            using UnityEngine;

            namespace Game.Lab
            {
                public class Spinner : MonoBehaviour
                {
                    [SerializeReference] private IStageBehaviour _behaviour;
                }
            }

            """);
        Write("Assets/Scripts/Lab/Spinner.cs.meta", Meta(SpinnerGuid, "MonoImporter"));
        Write("Assets/Scripts/Lab/Unheld.cs", """
            using UnityEngine;

            namespace Game.Lab
            {
                public class Unheld : MonoBehaviour
                {
                    [SerializeField] private int _count;
                }
            }

            """);
        Write("Assets/Scripts/Lab/Unheld.cs.meta", Meta("c0000000000000000000000000000005", "MonoImporter"));
        Write("Assets/Prefabs/Spinner.prefab", $$"""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1 &100
            GameObject:
              m_ObjectHideFlags: 0
              m_CorrespondingSourceObject: {fileID: 0}
              m_PrefabInstance: {fileID: 0}
              m_PrefabAsset: {fileID: 0}
              serializedVersion: 6
              m_Component:
              - component: {fileID: 400}
              - component: {fileID: 110}
              m_Layer: 0
              m_Name: Spinner
              m_IsActive: 1
            --- !u!4 &400
            Transform:
              m_ObjectHideFlags: 0
              m_CorrespondingSourceObject: {fileID: 0}
              m_PrefabInstance: {fileID: 0}
              m_PrefabAsset: {fileID: 0}
              m_GameObject: {fileID: 100}
              serializedVersion: 2
              m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
              m_LocalPosition: {x: 0, y: 0, z: 0}
              m_LocalScale: {x: 1, y: 1, z: 1}
              m_ConstrainProportionsScale: 0
              m_Children: []
              m_Father: {fileID: 0}
              m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}
            --- !u!114 &110
            MonoBehaviour:
              m_ObjectHideFlags: 0
              m_CorrespondingSourceObject: {fileID: 0}
              m_PrefabInstance: {fileID: 0}
              m_PrefabAsset: {fileID: 0}
              m_GameObject: {fileID: 100}
              m_Enabled: 1
              m_EditorHideFlags: 0
              m_Script: {fileID: 11500000, guid: {{SpinnerGuid}}, type: 3}
              m_Name:
              m_EditorClassIdentifier: Assembly-CSharp::Game.Lab.Spinner
              _behaviour:
                rid: 1000
              references:
                version: 2
                RefIds:
                - rid: 1000
                  type: {class: SpinBehaviour, ns: Game.Core, asm: Game.Core}
                  data:
                    rate: 2

            """);
        Write("Assets/Prefabs/Spinner.prefab.meta", Meta("c0000000000000000000000000000004", "PrefabImporter"));
        AddToProject("Game.Core.csproj", @"Assets\Scripts\Core\Pool.cs", @"Assets\Scripts\Core\Behaviours.cs");
        AddToProject("Assembly-CSharp.csproj", @"Assets\Scripts\Lab\Turntable.cs", @"Assets\Scripts\Lab\Spinner.cs", @"Assets\Scripts\Lab\Unheld.cs");

        using GraphStore store = GraphStore.Open(Root, StoreMode.ReadWrite);
        CodeIndex.Update(CodeWorkspace.Load(Root, VendoredFolders.Default), store);
        AssetIndex.Update(AssetWorkspace.Scan(Root, VendoredFolders.Default), store, []);
    }

    public string Root { get; }

    /// <summary>A project path as the absolute path, with `/`, that a tool call names.</summary>
    public string Abs(string projectPath) => (Root + "/" + projectPath).Replace('\\', '/');

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

    private void Write(string projectPath, string text) => File.WriteAllText(Path.Combine(Root, projectPath), text.ReplaceLineEndings("\n"));

    private void AddToProject(string csproj, string after, params string[] files)
    {
        string path = Path.Combine(Root, csproj);
        string line = $"<Compile Include=\"{after}\" />";
        string text = File.ReadAllText(path);
        Assert.Contains(line, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(line, line + string.Concat(files.Select(f => $"\n    <Compile Include=\"{f}\" />")), StringComparison.Ordinal));
    }

    private static string Meta(string guid, string importer) =>
        $"fileFormatVersion: 2\nguid: {guid}\n{importer}:\n  externalObjects: {{}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n";

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
