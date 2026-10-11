using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// UI Toolkit in `ug shot` (SPEC-play-input, UI Toolkit, spike 3): a screen panel is drawn into the
    /// shot's texture by pointing its PanelSettings at it for one render, then pointing it back. A UXML
    /// shot draws through a hidden, never-saved UIDocument on a copy of a PanelSettings.
    /// </summary>
    internal static partial class ShotOps
    {
        private const string UxmlHolderName = "ug-shot-ui";

        private static readonly Color _noClear = new Color(0.2f, 0.2f, 0.2f, 1);

        // `, 2 UI Toolkit documents` and what was not drawn, for the Game view answer.
        private static string Drawn(int documents, int elsewhere, string problem)
        {
            string text = documents == 0 ? "" : ", " + Answer.Count(documents, "UI Toolkit document", "UI Toolkit documents");
            if (elsewhere > 0)
            {
                text += ", " + Answer.Count(elsewhere, "world-space or render-texture UI Toolkit document", "world-space or render-texture UI Toolkit documents") + " not drawn";
            }

            return problem == null ? text : text + ", UI Toolkit not drawn (" + problem + ")";
        }

        // One document's panel alone, on its clear colour or dark grey.
        private static Reply DocumentShot(UIDocument component, int width, int height)
        {
            UiToolkit.Document document = UiToolkit.DocumentFor(component, out string error);
            if (document == null)
            {
                return Answer.Error(error);
            }

            if (!document.OnScreen)
            {
                return Answer.Error(document.Address + "'s UIDocument is drawn into the world or its own texture: ug shot draws screen documents (ug shot game, or the camera that sees it)");
            }

            PanelSettings settings = UiToolkit.SettingsOf(component);
            List<string> sharing = UiToolkit.Documents(out _, out _)
                .Where(d => d.Component != component && UiToolkit.SettingsOf(d.Component) == settings)
                .Select(d => d.Address)
                .ToList();
            string what = "UI Toolkit document " + document.Address + (sharing.Count == 0 ? "" : ", with " + string.Join(", ", sharing) + " (same PanelSettings)");
            return Render(new List<Camera>(), width, height, Name("ui-", document.Address), what, false, new List<PanelSettings> { settings }, settings.clearColor ? settings.colorClearValue : _noClear);
        }

        // A UXML file without a scene object: a hidden document on a copy of the given (or the project's first) PanelSettings.
        private static Reply Uxml(string path, string panelPath, int width, int height)
        {
            VisualTreeAsset tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            if (tree == null)
            {
                string full = Path.Combine(Path.GetDirectoryName(Application.dataPath), path);
                return Answer.Error(File.Exists(full) ? path + " was not imported as a UXML (VisualTreeAsset): see ug console" : "no UXML at " + path);
            }

            PanelSettings source = null;
            if (panelPath != null)
            {
                source = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelPath);
                if (source == null)
                {
                    return Answer.Error("no PanelSettings at " + panelPath);
                }
            }
            else
            {
                string guid = AssetDatabase.FindAssets("t:PanelSettings").OrderBy(g => AssetDatabase.GUIDToAssetPath(g), StringComparer.Ordinal).FirstOrDefault();
                source = guid == null ? null : AssetDatabase.LoadAssetAtPath<PanelSettings>(AssetDatabase.GUIDToAssetPath(guid));
            }

            PanelSettings copy = source != null ? UnityEngine.Object.Instantiate(source) : ScriptableObject.CreateInstance<PanelSettings>();
            copy.hideFlags = HideFlags.HideAndDontSave;
            copy.targetTexture = null;
            string settingsText = source != null ? "PanelSettings " + AssetDatabase.GetAssetPath(source) : "a new PanelSettings";
            if (source == null)
            {
                string theme = AssetDatabase.FindAssets("t:ThemeStyleSheet").Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
                copy.themeStyleSheet = theme == null ? null : AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(theme);
                settingsText += theme == null ? " (no theme style sheet in the project: controls unstyled)" : " with " + theme;
            }

            GameObject holder = EditorUtility.CreateGameObjectWithHideFlags(UxmlHolderName, HideFlags.HideAndDontSave);
            try
            {
                UIDocument document = holder.AddComponent<UIDocument>();
                document.panelSettings = copy;
                document.visualTreeAsset = tree;
                document.enabled = false;
                document.enabled = true;
                return Render(new List<Camera>(), width, height, Name("ui-", Path.GetFileNameWithoutExtension(path)), "UXML " + path + ", " + settingsText, false,
                    new List<PanelSettings> { copy }, copy.clearColor ? copy.colorClearValue : _noClear);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(holder);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }

        // The panels drawn into the texture: their PanelSettings point at it for one layout and render, then back.
        // Null, or why UI Toolkit could not be drawn.
        private static string DrawPanels(List<PanelSettings> panels, RenderTexture texture)
        {
            var before = panels.Where(p => p != null).ToDictionary(p => p, p => p.targetTexture);
            try
            {
                foreach (PanelSettings settings in before.Keys)
                {
                    settings.targetTexture = texture;
                }

                return UiToolkit.Call("UpdatePanels") ?? UiToolkit.Call("RepaintPanels", true) ?? UiToolkit.Call("RenderOffscreenPanels");
            }
            finally
            {
                foreach (KeyValuePair<PanelSettings, RenderTexture> pair in before)
                {
                    if (pair.Key != null)
                    {
                        pair.Key.targetTexture = pair.Value;
                    }
                }

                UiToolkit.Call("UpdatePanels");
            }
        }
    }
}
