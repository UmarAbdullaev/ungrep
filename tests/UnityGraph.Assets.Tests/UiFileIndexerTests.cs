using UnityGraph.Core;
using UnityGraph.Core.Graph;

namespace UnityGraph.Assets.Tests;

/// <summary>UXML, USS and TSS references (SPEC-asset-index, UI Toolkit files), in a project written to a temp folder.</summary>
public sealed class UiFileIndexerTests : IDisposable
{
    private const string Menu = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01";
    private const string MenuStyle = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa02";
    private const string Card = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa03";
    private const string Background = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa04";
    private const string Icon = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa05";
    private const string IconSprite = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa06";
    private const string Font = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa07";
    private const string Theme = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa08";
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa09";
    private const string Elsewhere = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb01";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ug-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public UiFileIndexerTests()
    {
        Asset("Assets/UI/Menu.uxml", Menu, """
            <ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements">
                <Style src="Menu.uss" />
                <ui:Template name="Card" src="Parts/Card.uxml" />
                <ui:Instance template="Card" />
                <ui:VisualElement style="background-image: url(&apos;project://database/Assets/Art/bg.png?fileID=2800000&amp;guid=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa04&amp;type=3#bg&apos;);" />
                <ui:VisualElement style="background-image: resource(&apos;Icons/play&apos;);" />
                <ui:Label text="url(not a reference)" />
                <ui:Image source="project://database/Assets/Art/bg.png" />
                <ui:Button style="background-image: url(&apos;../Gone.png&apos;);" />
            </ui:UXML>
            """);
        Asset("Assets/UI/Menu.uss", MenuStyle, """
            /* url("Commented.png") is not read */
            @import url("Base.uss");
            .title {
                -unity-font-definition: url("/Assets/Fonts/Title.ttf");
                background-image: url(../Art/bg.png);
            }
            .play { background-image: resource("Icons/play"); }
            .cursor { cursor: url('unity-theme://default/cursor') 0 0; }
            .pkg { background-image: url("/Packages/com.unity.ugui/Runtime/x.png"); }
            """);
        Asset("Assets/UI/Base.uss", Base, "@import \"Menu.uss\";\n");
        Asset("Assets/UI/Parts/Card.uxml", Card, "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" />\n");
        Asset("Assets/UI/Game.tss", Theme, "@import url(\"unity-theme://default\");\n@import url(\"Menu.uss\");\nVisualElement {}\n");
        Asset("Assets/Art/bg.png", Background, "png");
        Asset("Assets/Fonts/Title.ttf", Font, "ttf");
        Asset("Assets/Resources/Icons/play.png", Icon, "png");
        Asset("Assets/Shop/Resources/Icons/play.asset", IconSprite, "asset");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
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

    private void Asset(string path, string guid, string text)
    {
        Write(path, text);
        Write(path + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
    }

    private (FileContribution File, List<string> Problems) Index(string path)
    {
        var problems = new List<string>();
        AssetWorkspace workspace = AssetWorkspace.Scan(_root, VendoredFolders.Default);
        FileContribution file = AssetFileIndexer.Content(_root, path, problems, workspace.Resources);
        return (file, problems);
    }

    private static string[] Rows(FileContribution file) =>
        [.. file.Edges.Select(e => $"{KindNames.Of(e.Kind)} {e.Dst} {e.Confidence.ToString().ToLowerInvariant()} @{e.Line} {e.Props}")];

    [Fact]
    public void Uxml_references_its_templates_styles_and_assets()
    {
        (FileContribution file, List<string> problems) = Index("Assets/UI/Menu.uxml");

        Assert.Equal(FileStatus.Ok, file.File.Status);
        Assert.Empty(file.Nodes);
        Assert.All(file.Edges, e => Assert.Equal("a:" + Menu, e.Src));
        Assert.Equal(
            [
                $"references a:{MenuStyle} exact @2 {{\"field\":\"Style\"}}",
                $"references a:{Card} exact @3 {{\"field\":\"Template\"}}",
                $"references a:{Background} exact @5 {{\"field\":\"style\"}}",
                $"references a:{Icon} heuristic @6 {{\"field\":\"style\"}}",
                $"references a:{IconSprite} heuristic @6 {{\"field\":\"style\"}}",
                $"references a:{Background} exact @8 {{\"field\":\"source\"}}",
            ],
            Rows(file));
        Assert.Equal(["Assets/UI/Menu.uxml:9: ../Gone.png not found"], problems);
    }

    [Fact]
    public void Uss_references_imports_urls_and_resources_and_skips_comments_built_ins_and_cached_packages()
    {
        (FileContribution file, List<string> problems) = Index("Assets/UI/Menu.uss");

        Assert.Equal(
            [
                $"references a:{Base} exact @2 {{\"field\":\"@import\"}}",
                $"references a:{Font} exact @4 {{\"field\":\"url\"}}",
                $"references a:{Background} exact @5 {{\"field\":\"url\"}}",
                $"references a:{Icon} heuristic @7 {{\"field\":\"resource\"}}",
                $"references a:{IconSprite} heuristic @7 {{\"field\":\"resource\"}}",
            ],
            Rows(file));
        Assert.Empty(problems);
    }

    [Fact]
    public void A_string_import_and_a_theme_import_resolve_like_urls()
    {
        Assert.Equal([$"references a:{MenuStyle} exact @1 {{\"field\":\"@import\"}}"], Rows(Index("Assets/UI/Base.uss").File));

        (FileContribution theme, List<string> problems) = Index("Assets/UI/Game.tss");
        Assert.Equal([$"references a:{MenuStyle} exact @2 {{\"field\":\"@import\"}}"], Rows(theme));
        Assert.Empty(problems);
    }

    [Fact]
    public void A_uxml_that_is_not_well_formed_is_an_error_without_rows()
    {
        Asset("Assets/UI/Broken.uxml", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa10", "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n  <ui:Button>\n</ui:UXML>\n");

        (FileContribution file, List<string> problems) = Index("Assets/UI/Broken.uxml");

        Assert.Equal(FileStatus.Error, file.File.Status);
        Assert.Empty(file.Edges);
        Assert.StartsWith("Assets/UI/Broken.uxml:3: not well-formed XML (", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_resource_and_a_target_without_meta_are_problems()
    {
        Write("Assets/Art/raw.png", "png");
        Asset("Assets/UI/Odd.uss", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa11", ".a { background-image: resource(\"Nope\"); }\n.b { background-image: url(\"../Art/raw.png\"); }\n");

        (FileContribution file, List<string> problems) = Index("Assets/UI/Odd.uss");

        Assert.Empty(file.Edges);
        Assert.Equal(["Assets/UI/Odd.uss:1: resource Nope is in no Resources folder", "Assets/UI/Odd.uss:2: Assets/Art/raw.png has no .meta with a guid"], problems);
    }

    [Fact]
    public void A_project_database_uri_with_a_guid_needs_no_file()
    {
        Asset("Assets/UI/Far.uxml", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa12",
            $"<ui:UXML xmlns:ui=\"UnityEngine.UIElements\">\n  <Style src=\"project://database/Assets/Elsewhere/X.uss?fileID=7433441132597879392&amp;guid={Elsewhere}&amp;type=3#X\" />\n</ui:UXML>\n");

        (FileContribution file, List<string> problems) = Index("Assets/UI/Far.uxml");

        Assert.Equal([$"references a:{Elsewhere} exact @2 {{\"field\":\"Style\"}}"], Rows(file));
        Assert.Empty(problems);
    }

    [Fact]
    public void The_workspace_tracks_ui_files_and_indexes_them_with_its_resources()
    {
        AssetWorkspace workspace = AssetWorkspace.Scan(_root, VendoredFolders.Default);

        Assert.True(workspace.IsTracked("Assets/UI/Menu.uxml"));
        Assert.True(workspace.IsTracked("Assets/UI/Menu.uss"));
        Assert.True(workspace.IsTracked("Assets/UI/Game.tss"));
        Assert.Equal(["Assets/Resources/Icons/play.png", "Assets/Shop/Resources/Icons/play.asset"], workspace.Resources("Icons/play"));
        FileContribution menu = Assert.Single(workspace.IndexMany(["Assets/UI/Menu.uss"]));
        Assert.Contains(menu.Edges, e => e.Dst == "a:" + IconSprite && e.Confidence == Confidence.Heuristic);
    }

    [Fact]
    public void A_ui_document_and_its_panel_settings_reference_the_uxml_panel_and_theme_through_yaml()
    {
        const string Panel = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa13";
        Write("Assets/UI/Panel.asset.meta", "fileFormatVersion: 2\nguid: " + Panel + "\nNativeFormatImporter:\n  mainObjectFileID: 11400000\n");
        Write("Assets/UI/Panel.asset", $"""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!114 &11400000
            MonoBehaviour:
              m_GameObject: {"{"}fileID: 0{"}"}
              m_Script: {"{"}fileID: 19101, guid: 0000000000000000e000000000000000, type: 0{"}"}
              m_Name: Panel
              themeUss: {"{"}fileID: -4733365628477956816, guid: {Theme}, type: 3{"}"}

            """);
        Write("Assets/Menu.prefab.meta", "fileFormatVersion: 2\nguid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa14\nPrefabImporter:\n");
        Write("Assets/Menu.prefab", $"""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!1 &1
            GameObject:
              m_Component:
              - component: {"{"}fileID: 2{"}"}
              m_Name: Menu
              m_IsActive: 1
            --- !u!114 &2
            MonoBehaviour:
              m_GameObject: {"{"}fileID: 1{"}"}
              m_Enabled: 1
              m_Script: {"{"}fileID: 19102, guid: 0000000000000000e000000000000000, type: 0{"}"}
              m_EditorClassIdentifier: UnityEngine.UIElementsModule::UnityEngine.UIElements.UIDocument
              m_PanelSettings: {"{"}fileID: 11400000, guid: {Panel}, type: 2{"}"}
              m_ParentUI: {"{"}fileID: 0{"}"}
              sourceAsset: {"{"}fileID: 9197481963319205126, guid: {Menu}, type: 3{"}"}

            """);

        (FileContribution panel, List<string> panelProblems) = Index("Assets/UI/Panel.asset");
        (FileContribution prefab, List<string> prefabProblems) = Index("Assets/Menu.prefab");

        Assert.Empty(panelProblems);
        Assert.Empty(prefabProblems);
        Assert.Contains(panel.Edges, e => e.Kind == EdgeKind.References && e.Dst == "a:" + Theme && e.Props!.Contains("\"field\":\"themeUss\"", StringComparison.Ordinal));
        Assert.Contains(prefab.Edges, e => e.Kind == EdgeKind.References && e.Dst == "a:" + Panel && e.Props!.Contains("\"field\":\"m_PanelSettings\"", StringComparison.Ordinal));
        Assert.Contains(prefab.Edges, e => e.Kind == EdgeKind.References && e.Dst == "a:" + Menu && e.Props!.Contains("\"field\":\"sourceAsset\"", StringComparison.Ordinal));
    }
}
