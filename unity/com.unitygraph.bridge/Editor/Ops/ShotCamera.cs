using System;
using UnityEditor;
using UnityEngine;

namespace UnityGraph.Bridge
{
    /// <summary>
    /// The camera made for one `ug shot` viewpoint or sheet (SPEC-editor-ops, Viewpoints): hidden and
    /// never saved, a copy of a source camera's settings, disabled so it draws only when asked, and
    /// destroyed after the render. Nothing goes through Undo, so the history and the scene's dirty flag
    /// stay as they were.
    /// </summary>
    internal sealed class ShotCamera : IDisposable
    {
        public const string ObjectName = "ug shot camera";

        public ShotCamera(Camera source)
        {
            GameObject holder = EditorUtility.CreateGameObjectWithHideFlags(ObjectName, HideFlags.HideAndDontSave, typeof(Camera));
            Camera = holder.GetComponent<Camera>();
            if (source != null)
            {
                Camera.CopyFrom(source);
                CopyPipelineData?.Invoke(source, Camera);
            }

            // CopyFrom brings the source's target texture and viewport; the shot renders the whole of its own texture.
            Camera.enabled = false;
            Camera.targetTexture = null;
            Camera.rect = new Rect(0, 0, 1, 1);
            Camera.cameraType = CameraType.Game;
        }

        /// <summary>Copies a render pipeline's per-camera data from the source to the shot camera; set by the Urp assembly.</summary>
        internal static Action<Camera, Camera> CopyPipelineData { get; set; }

        public Camera Camera { get; }

        public void Dispose()
        {
            if (Camera != null)
            {
                UnityEngine.Object.DestroyImmediate(Camera.gameObject);
            }
        }
    }
}
