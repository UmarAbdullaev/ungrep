using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Ungrep.Bridge
{
    /// <summary>
    /// URP keeps a camera's renderer, post-processing, anti-aliasing and render type on
    /// UniversalAdditionalCameraData beside the Camera. A `ug shot` viewpoint copies it from the
    /// source camera so the picture looks like the game; the shot camera is always a Base camera
    /// with no stack of its own (SPEC-editor-ops, Viewpoints).
    /// </summary>
    [InitializeOnLoad]
    internal static class UrpShotCamera
    {
        static UrpShotCamera()
        {
            ShotCamera.CopyPipelineData = Copy;
        }

        private static void Copy(Camera source, Camera shot)
        {
            if (!source.TryGetComponent(out UniversalAdditionalCameraData from))
            {
                return;
            }

            UniversalAdditionalCameraData to = shot.GetUniversalAdditionalCameraData();
            EditorUtility.CopySerialized(from, to);
            to.hideFlags = HideFlags.HideAndDontSave;
            to.renderType = CameraRenderType.Base;
            to.cameraStack?.Clear();
        }
    }
}
