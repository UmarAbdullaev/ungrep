using System.Diagnostics;

namespace Ungrep.Cli.Tests;

/// <summary>
/// The UI Toolkit part of the end-to-end run (SPEC-play-input, UI Toolkit): a UXML and a probe that reacts to its button
/// are written before the editor opens; ug asset create makes the PanelSettings, ug component add and ug set the UIDocument.
/// </summary>
public sealed partial class EditorEndToEndTests
{
    private const string MenuUxml = """
        <ui:UXML xmlns:ui="UnityEngine.UIElements">
            <ui:VisualElement name="Panel" style="position: absolute; left: 10px; top: 10px; width: 200px;">
                <ui:Button name="Go" text="Go" style="height: 30px;" />
                <ui:Button name="Back" text="Back" style="height: 30px;" />
                <ui:Slider name="Volume" low-value="0" high-value="1" value="0.5" />
            </ui:VisualElement>
        </ui:UXML>
        """;

    private const string UiProbeSource = """
        using UnityEngine;

        namespace BridgeHost
        {
            public class UiProbe : MonoBehaviour
            {
                void Start()
                {
                    UnityEngine.UIElements.VisualElement root = GetComponent<UnityEngine.UIElements.UIDocument>().rootVisualElement;
                    UnityEngine.UIElements.UQueryExtensions.Q<UnityEngine.UIElements.Button>(root, "Go").clicked += () =>
                    {
                        Debug.LogError("probe: go clicked");
                        new GameObject("Went");
                    };
                }
            }
        }
        """;

    private void PrepareUiToolkit()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "UI"));
        File.WriteAllText(Path.Combine(_root, "Assets", "UI", "Menu.uxml"), MenuUxml.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scripts", "UiProbe.cs"), UiProbeSource.ReplaceLineEndings("\n"));
    }

    // UI Toolkit: set-up through ug asset create, ug component add and ug set; ug tree --ui, ug inspect and ug ui in edit
    // mode; ug click, ug click --value and ug nav in play mode. -nographics has no device, so ug shot only refuses.
    private void UiToolkitCommands(string log)
    {
        var clock = Stopwatch.StartNew();
        string panel = Ug(log, "asset", "create", "Assets/UI/Panel.asset", "PanelSettings");
        Report("ug asset create (PanelSettings)", clock.Elapsed);
        Assert.StartsWith("created Assets/UI/Panel.asset (UnityEngine.UIElements.PanelSettings; ", panel, StringComparison.Ordinal);
        Assert.StartsWith("created Assets/UI/Menu.uss (", Ug(log, "asset", "create", "Assets/UI/Menu.uss"), StringComparison.Ordinal);
        Assert.StartsWith("created Menu", Ug(log, "object", "create", "Menu"), StringComparison.Ordinal);
        Assert.StartsWith("added UIDocument to Menu", Ug(log, "component", "add", "Menu", "UIDocument"), StringComparison.Ordinal);
        Assert.StartsWith("Menu  UIDocument.", Ug(log, "set", "Menu", "panelSettings", "Assets/UI/Panel.asset", "--component", "UIDocument"), StringComparison.Ordinal);
        Assert.StartsWith("Menu  UIDocument.sourceAsset  ", Ug(log, "set", "Menu", "sourceAsset", "Assets/UI/Menu.uxml", "--component", "UIDocument"), StringComparison.Ordinal);
        Assert.StartsWith("added UiProbe to Menu", Ug(log, "component", "add", "Menu", "UiProbe"), StringComparison.Ordinal);

        clock.Restart();
        string tree = Ug(log, "tree", "--ui", "Menu");
        Report("ug tree --ui", clock.Elapsed);
        Tokens("ug tree --ui answer", tree);
        Assert.StartsWith("Menu  UIDocument  Assets/UI/Menu.uxml  PanelSettings Assets/UI/Panel.asset  sort 0\n", tree, StringComparison.Ordinal);
        Assert.Matches(@"\n\s+Go  Button  ""Go""", tree);

        clock.Restart();
        string edit = Ug(log, "ui");
        Report("ug ui (edit mode, UI Toolkit)", clock.Elapsed);
        Tokens("ug ui answer with a UI Toolkit document", edit);
        Assert.Matches(@"^ui  \d+x\d+  edit mode  (1 canvas, )?1 UI Toolkit document", edit);
        Assert.Matches(@"\nMenu/Go  button  ""Go""  \d+,\d+  \d+x\d+\n", edit);
        Assert.StartsWith("Menu/Go  UnityEngine.UIElements.Button  \"Go\"  (Menu: Assets/UI/Menu.uxml)\n", Ug(log, "inspect", "Menu/Go"), StringComparison.Ordinal);
        Assert.Equal("not playing: ug play first\n", Ug(log, "click", "Menu/Go"));
        Assert.Equal("not playing: ug play first\n", Ug(log, "nav", "down"));

        Ug(log, "play");
        clock.Restart();
        string click = Ug(log, "click", "Menu/Go");
        Report("ug click (UI Toolkit, digest)", clock.Elapsed);
        Tokens("ug click answer (UI Toolkit)", click);
        Assert.Matches(@"^clicked Menu/Go \(button ""Go""\) at \d+,\d+\n  now: 1 error; changed Went \(new\)\n  error  probe: go clicked", click);
        Assert.Matches(@"^set Menu/Volume \(slider\) 0\.5 -> 0\.25\n", Ug(log, "click", "Menu/Volume", "--value", "0.25"));

        clock.Restart();
        string nav = Ug(log, "nav", "down", "Menu/Go");
        Report("ug nav (UI Toolkit)", clock.Elapsed);
        Assert.StartsWith("nav down: Menu/Go -> Menu/Back\n", nav, StringComparison.Ordinal);
        Assert.StartsWith("nav submit on Menu/Go (button \"Go\")\n  now: 1 error", Ug(log, "nav", "submit", "Menu/Go"), StringComparison.Ordinal);
        Assert.StartsWith("cannot take a screenshot: this Unity runs without a graphics device", Ug(log, "shot", "Menu"), StringComparison.Ordinal);

        // The probe's two errors, and -nographics adds "No graphic device is available" ones.
        Assert.Matches(@"^stopped after \d+\.\d s: \d+ errors, 0 warnings", Ug(log, "stop"));
    }
}
