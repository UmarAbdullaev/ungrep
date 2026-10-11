using System.Diagnostics;

namespace Ungrep.Cli.Tests;

/// <summary>
/// The play-input part of the end-to-end run (SPEC-play-input, Testing): the BridgeHost copy gets com.unity.ugui,
/// the Input System from Unity's own package cache (a local path, no download), Active Input Handling "Both",
/// and a probe script that reacts to a click, a key, a stick, a tap and a scene load.
/// </summary>
public sealed partial class EditorEndToEndTests
{
    private const string ProbeSource = """
        using UnityEngine;
        using UnityEngine.EventSystems;
        using UnityEngine.InputSystem;
        using UnityEngine.SceneManagement;
        using UnityEngine.UI;

        namespace BridgeHost
        {
            public class InputProbe : MonoBehaviour
            {
                public Button start;
                public GameObject gem;

                // Edit mode, through ug run: a canvas with a button, a covered button and a slider, an EventSystem, and the probe.
                public static string Build()
                {
                    var canvas = new GameObject("Canvas", typeof(Canvas), typeof(GraphicRaycaster));
                    canvas.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                    var probe = new GameObject("Probe", typeof(InputProbe)).GetComponent<InputProbe>();
                    probe.gem = new GameObject("Gem");
                    probe.start = MakeButton(canvas.transform, "Start", 0.7f);
                    MakeButton(canvas.transform, "Covered", 0.4f);
                    Place(canvas.transform, "Blocker", 0.4f, 240, 80).gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0);
                    RectTransform volume = Place(canvas.transform, "Volume", 0.2f, 160, 20);
                    volume.gameObject.AddComponent<Image>();
                    volume.gameObject.AddComponent<Slider>().value = 0.5f;
                    new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                    return "built";
                }

                static RectTransform Place(Transform parent, string name, float y, float width, float height)
                {
                    var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
                    rect.SetParent(parent, false);
                    rect.anchorMin = rect.anchorMax = new Vector2(0.5f, y);
                    rect.sizeDelta = new Vector2(width, height);
                    return rect;
                }

                static Button MakeButton(Transform parent, string label, float y)
                {
                    RectTransform rect = Place(parent, label, y, 160, 40);
                    rect.gameObject.AddComponent<Image>();
                    var text = new GameObject("Label", typeof(RectTransform), typeof(Text)).GetComponent<Text>();
                    text.transform.SetParent(rect, false);
                    text.text = label;
                    text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    text.raycastTarget = false;
                    return rect.gameObject.AddComponent<Button>();
                }

                void Start()
                {
                    if (start != null)
                    {
                        start.onClick.AddListener(() =>
                        {
                            Debug.LogError("probe: start clicked");
                            new GameObject("Spawned");
                            transform.position += Vector3.forward;
                        });
                    }
                }

                void Update()
                {
                    Keyboard keyboard = Keyboard.current;
                    if (keyboard != null && keyboard.spaceKey.wasPressedThisFrame)
                    {
                        transform.position += Vector3.up;
                    }

                    if (keyboard != null && keyboard.eKey.wasPressedThisFrame)
                    {
                        SceneManager.CreateScene("Extra");
                    }

                    Gamepad pad = Gamepad.current;
                    if (pad != null && pad.buttonSouth.wasPressedThisFrame)
                    {
                        gem.SetActive(!gem.activeSelf);
                    }

                    if (pad != null && pad.leftStick.ReadValue().x > 0.5f)
                    {
                        transform.position += Vector3.right * 0.1f;
                    }

                    Touchscreen touch = Touchscreen.current;
                    if (touch != null && touch.primaryTouch.press.wasPressedThisFrame)
                    {
                        Vector2 at = touch.primaryTouch.position.ReadValue();
                        new GameObject("Tap_" + Mathf.RoundToInt(at.x) + "_" + Mathf.RoundToInt(at.y));
                    }
                }
            }
        }
        """;

    // com.unity.ugui (built in), the Input System from Unity's package cache, and Active Input Handling "Both",
    // in the copy only. False when the cache has no Input System: that part is then skipped.
    private bool PrepareInput(string manifest)
    {
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "cache", "packages", "packages.unity.com", "com.unity.inputsystem@1.14.2");
        if (!Directory.Exists(cache))
        {
            TestContext.Current.TestOutputHelper?.WriteLine("play-input end to end skipped: no Input System in " + cache);
            return false;
        }

        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace(
            "\"dependencies\": {",
            "\"dependencies\": {\n    \"com.unity.inputsystem\": \"file:" + cache.Replace('\\', '/') + "\",\n    \"com.unity.ugui\": \"2.0.0\",",
            StringComparison.Ordinal));
        string settings = Path.Combine(_root, "ProjectSettings", "ProjectSettings.asset");
        File.WriteAllText(settings, File.ReadAllText(settings).Replace("activeInputHandler: 0", "activeInputHandler: 2", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(_root, "Assets", "Scripts", "InputProbe.cs"), ProbeSource.ReplaceLineEndings("\n"));
        return true;
    }

    // play-input: ug ui, ug click, the device actions and their digests, ug wait, ug play --step, a batch, a hold cut short by ug stop.
    private void PlayInputCommands(string log, string repo)
    {
        var clock = Stopwatch.StartNew();
        // The probe was written before the editor opened, so it is usually compiled already.
        Assert.Matches(@"^(compiled in \d+\.\d s|no script changes): 0 errors\n$", Ug(log, "compile"));
        Assert.Matches(@"^ran BridgeHost\.InputProbe\.Build\(\) in [\d.]+ ms\n  result: ""built""\n(  changed: .+\n)?$", Ug(log, "run", "BridgeHost.InputProbe.Build"));

        // In edit mode ug ui lists, every action is refused, and nothing is held.
        Assert.Equal("not playing: ug play first\n", Ug(log, "press", "Space"));
        Assert.Matches(@"^ui  \d+x\d+  edit mode  1 canvas\n(.*\n)*Canvas/Start  button  ""Start""", Ug(log, "ui"));
        Assert.Equal("not playing: ug play first\n", Ug(log, "nav", "submit"));
        Assert.Equal("not playing: ug play first\n", Ug(log, "click", "Start"));

        Ug(log, "play");

        clock.Restart();
        string ui = Ug(log, "ui");
        Report("ug ui", clock.Elapsed);
        Tokens("ug ui answer", ui);
        Assert.Matches(@"^ui  \d+x\d+  playing  1 canvas\n", ui);
        Assert.Matches(@"\nCanvas/Start  button  ""Start""  \d+,\d+  160x40\n", ui);
        Assert.Matches(@"\nCanvas/Volume  slider  0\.5 \(0-1\)  \d+,\d+  160x20\n", ui);
        Assert.Contains("  1 more not usable (1 blocked): ug ui --all\n", ui, StringComparison.Ordinal);
        Assert.Matches(@"\nCanvas/Covered  button  ""Covered""  .*  blocked by Canvas/Blocker\n", Ug(log, "ui", "--all"));

        clock.Restart();
        string click = Ug(log, "click", "Start");
        Report("ug click (digest)", clock.Elapsed);
        Tokens("ug click answer", click);
        Assert.Matches(@"^clicked Canvas/Start \(button ""Start""\) at \d+,\d+\n  now: 1 error; changed Probe \(moved\), Spawned \(new\)\n  error  probe: start clicked", click);

        Assert.Matches(@"^blocked: Canvas/Blocker is on top of Canvas/Covered at \d+,\d+\n$", Ug(log, "click", "Covered"));
        Assert.Equal("set Canvas/Volume (slider) 0.5 -> 0.25\n  now: nothing changed\n", Ug(log, "click", "Volume", "--value", "0.25"));
        Assert.Equal("7 is outside Canvas/Volume's range 0-1\n", Ug(log, "click", "Volume", "--value", "7"));

        clock.Restart();
        string space = Ug(log, "press", "Space");
        Report("ug press Space (digest)", clock.Elapsed);
        Assert.Equal("pressed Space for 0.1 s\n  now: changed Probe (moved)\n", space);
        Assert.Equal("pressed pad:south for 0.1 s\n  now: changed Gem (inactive)\n", Ug(log, "press", "pad:south"));
        Assert.Equal("stick left 1,0 for 0.3 s\n  now: changed Probe (moved)\n", Ug(log, "stick", "left", "1,0", "--hold", "0.3"));
        Assert.Equal("tapped 100,100 for 0.1 s\n  now: changed Tap_100_380 (new)\n", Ug(log, "tap", "100,100"));
        Assert.Equal("pressed E for 0.1 s\n  now: scene +Extra\n", Ug(log, "press", "E"));
        Assert.StartsWith("no key or button Spase; near: Space", Ug(log, "press", "Spase"), StringComparison.Ordinal);

        Assert.Equal("waited 1.0 s\n  now: nothing changed\n", Ug(log, "wait", "1"));
        string shot = Ug(log, "wait", "0.1", "--shot");
        TestContext.Current.TestOutputHelper?.WriteLine("ug wait 0.1 --shot:\n" + shot);
        Assert.StartsWith("waited 0.1 s\n  now: nothing changed\n", shot, StringComparison.Ordinal);

        Assert.StartsWith("paused", Ug(log, "pause"), StringComparison.Ordinal);
        Assert.Equal("paused: input is read on frames; ug play resumes, ug play --step <n> advances\n", Ug(log, "press", "Space"));
        Assert.Matches(@"^stepped 3 frames \(frame \d+\)\n  now: nothing changed\n$", Ug(log, "play", "--step", "3"));
        Ug(log, "play");

        clock.Restart();
        string batch = Ug(log, "batch", "press Space", "stick left 1,0 --hold 0.2", "tap 120,120", "wait 0.2");
        Report("ug batch of 4 actions", clock.Elapsed);
        Tokens("ug batch answer", batch);
        Assert.Contains("4/4 wait 0.2\nwaited 0.2 s\n", batch, StringComparison.Ordinal);
        Assert.Contains("4 steps done.", batch, StringComparison.Ordinal);

        // A hold that ug stop cuts short is released and says so; the request of ug press answers then.
        string ugExe = Path.Combine(repo, "artifacts", "bin", "Ungrep.Cli", "release", OperatingSystem.IsWindows() ? "ug.exe" : "ug");
        if (File.Exists(ugExe))
        {
            var info = new ProcessStartInfo(ugExe) { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (string arg in new[] { "press", "Space", "--hold", "30", "--project", _root })
            {
                info.ArgumentList.Add(arg);
            }

            using Process held = Process.Start(info)!;
            Thread.Sleep(1500);
            Assert.Matches(@"^stopped after \d+\.\d s: ", Ug(log, "stop"));
            string cut = held.StandardOutput.ReadToEnd().ReplaceLineEndings("\n");
            held.WaitForExit();
            Assert.Matches(@"^press Space: play mode ended after \d+\.\d+ s, released\n$", cut);
        }
        else
        {
            Ug(log, "stop");
        }

        Assert.Equal("not playing: ug play first\n", Ug(log, "press", "Space"));
        Assert.EndsWith("\nedit mode\n", Ug(log, "scene"), StringComparison.Ordinal);
    }
}
