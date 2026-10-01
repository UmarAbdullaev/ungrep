using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// `/shot` (SPEC-editor-ops, Endpoints): renders the Game view's cameras, the Scene view or one
    /// camera to a PNG under Library/unitygraph/shots/. Claude reads the file only when it needs to
    /// see. Nothing in the project changes; cameras get their target texture back.
    /// </summary>
    internal static class ShotOps
    {
        public const int DefaultWidth = 960;
        public const int DefaultHeight = 540;
        public const int MinSize = 64;
        public const int MaxSize = 2048;

        public static Reply Shot(Request request)
        {
            string view = string.IsNullOrWhiteSpace(request.Get("view")) ? "game" : request.Get("view").Trim();
            int height = DefaultHeight;
            Reply refused = Size(request.Get("width"), DefaultWidth, "width", out int width);
            refused = refused ?? Size(request.Get("height"), DefaultHeight, "height", out height);
            if (refused != null)
            {
                return refused;
            }

            if ((view == "game" || view == "scene") && NoGraphics() is Reply none)
            {
                return none;
            }

            var cameras = new List<Camera>();
            string what;
            if (view == "game")
            {
                cameras = Cameras().ToList();
                if (cameras.Count == 0)
                {
                    return Answer.Error("no enabled camera in the loaded scenes: nothing to show in the Game view (ug object create --primitive ... or add a Camera)");
                }

                what = "Game view, " + Answer.Count(cameras.Count, "camera", "cameras");
            }
            else if (view == "scene")
            {
                SceneView sceneView = SceneView.lastActiveSceneView ?? SceneView.sceneViews.Cast<SceneView>().FirstOrDefault();
                if (sceneView == null || sceneView.camera == null)
                {
                    return Answer.Error("no Scene view is open in the editor: open one (Window > General > Scene) or use ug shot game");
                }

                cameras.Add(sceneView.camera);
                what = "Scene view";
            }
            else
            {
                GameObject target = Addresses.Find(view, out string error);
                if (target == null)
                {
                    return Answer.Error(error);
                }

                Camera camera = target.GetComponent<Camera>();
                if (camera == null)
                {
                    return Answer.Error(Addresses.Of(target) + " has no Camera component");
                }

                cameras.Add(camera);
                what = "camera " + Addresses.Of(target);
            }

            return NoGraphics() ?? Render(cameras, width, height, Name(view), what);
        }

        // A batch-mode editor started with -nographics has no graphics device: nothing can be drawn.
        private static Reply NoGraphics() =>
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                ? Answer.Error("cannot take a screenshot: this Unity runs without a graphics device (started with -batchmode -nographics); open the editor normally")
                : null;

        // Null when the value is fine (or missing: the default), else the refusal.
        private static Reply Size(string text, int fallback, string name, out int size)
        {
            size = fallback;
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < MinSize || size > MaxSize)
            {
                return Answer.Error(name + " must be a whole number from " + MinSize + " to " + MaxSize + ", not " + text);
            }

            return null;
        }

        // The Game view draws every enabled camera in depth order; cameras that render into a texture are not on screen.
        private static IEnumerable<Camera> Cameras() =>
            Camera.allCameras.Where(camera => camera.enabled && camera.gameObject.scene.IsValid() && camera.targetTexture == null && camera.cameraType == CameraType.Game)
                .OrderBy(camera => camera.depth);

        private static Reply Render(List<Camera> cameras, int width, int height, string name, string what)
        {
            RenderTexture texture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            var saved = cameras.ToDictionary(camera => camera, camera => camera.targetTexture);
            List<Action> restore = what.StartsWith("Scene view", StringComparison.Ordinal) ? new List<Action>() : DrawOverlays(cameras[cameras.Count - 1]);
            Texture2D image = null;
            try
            {
                foreach (Camera camera in cameras)
                {
                    camera.targetTexture = texture;
                    camera.Render();
                }

                RenderTexture.active = texture;
                image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();
                byte[] png = image.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    return Answer.Error("Unity could not encode the screenshot as a PNG");
                }

                string relative = "Library/unitygraph/shots/" + name + ".png";
                string full = Path.Combine(Path.GetDirectoryName(Application.dataPath), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllBytes(full, png);
                return Answer.Line(relative + "  " + width + "x" + height + "  " + Kilobytes(png.Length) + "  (" + what + (EditorApplication.isPlaying ? ", playing" : "") + Undrawn(what) + ")");
            }
            finally
            {
                foreach (Action undo in restore)
                {
                    undo();
                }

                foreach (KeyValuePair<Camera, RenderTexture> pair in saved)
                {
                    if (pair.Key != null)
                    {
                        pair.Key.targetTexture = pair.Value;
                    }
                }

                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(texture);
                if (image != null)
                {
                    UnityEngine.Object.DestroyImmediate(image);
                }
            }
        }

        // UI Toolkit panels are drawn by the screen too, and Camera.Render has no way to include them: say so,
        // so a shot without the UI is not read as a UI that is missing.
        private static string Undrawn(string what)
        {
            if (what.StartsWith("Scene view", StringComparison.Ordinal))
            {
                return "";
            }

            int documents = UnityEngine.Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None).Count(d => d.isActiveAndEnabled);
            return documents == 0 ? "" : ", " + Answer.Count(documents, "UI Toolkit document", "UI Toolkit documents") + " not drawn";
        }

        // Screen Space - Overlay canvases are drawn by the screen, not by any camera, so Camera.Render leaves
        // them out. For the render each root overlay canvas is pointed at the last camera and put back after
        // (nothing is registered with Undo or marked dirty). Returns the actions that put them back.
        private static List<Action> DrawOverlays(Camera camera)
        {
            var restore = new List<Action>();
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!canvas.isRootCanvas || canvas.renderMode != RenderMode.ScreenSpaceOverlay || !canvas.gameObject.scene.IsValid() || !canvas.gameObject.scene.isLoaded)
                {
                    continue;
                }

                Canvas held = canvas;
                float distance = held.planeDistance;
                Camera before = held.worldCamera;
                held.renderMode = RenderMode.ScreenSpaceCamera;
                held.worldCamera = camera;
                held.planeDistance = Mathf.Min(camera.nearClipPlane + 0.5f, (camera.nearClipPlane + camera.farClipPlane) / 2);
                restore.Add(() =>
                {
                    if (held != null)
                    {
                        held.worldCamera = before;
                        held.renderMode = RenderMode.ScreenSpaceOverlay;
                        held.planeDistance = distance;
                    }
                });
            }

            if (restore.Count > 0)
            {
                Canvas.ForceUpdateCanvases();
            }

            return restore;
        }

        // game.png, scene.png, cam-Player-Camera.png: the file name for a view, kept to characters every file system takes.
        public static string Name(string view)
        {
            if (view == "game" || view == "scene")
            {
                return view;
            }

            var name = new StringBuilder("camera-");
            foreach (char c in view)
            {
                name.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
            }

            return name.ToString().Substring(0, Math.Min(name.Length, 60));
        }

        private static string Kilobytes(int bytes) => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
    }
}
