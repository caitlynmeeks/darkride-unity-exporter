// A picture of the scene from its camera, rendered by Unity as it is, for the side by side with darkride
// (LIGHTMAPS.md 3.9): the camera's post-processing applies, so what Unity shows is what the file holds.

using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Darkride.Exporter
{
    public static class Screenshot
    {
        public static Camera SceneCamera()
        {
            var main = Camera.main;
            if (main != null && main.gameObject.activeInHierarchy) return main;
            foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (camera.enabled && camera.gameObject.activeInHierarchy) return camera;
            return null;
        }

        // A batch-mode session's first renders ignore the baked reflection probes: the probe diagnostic of 2026-10-17
        // (ProbeDiagnostic.cs) rendered chrome black through five renders, with the probe disabled or given a red
        // cube map all the same, until a change to the environment reflection settings made Unity rebuild its
        // reflection state; then the probe applied and the chrome reflected the room. So the environment is
        // brought up to date before a picture is taken
        public static void RefreshReflections()
        {
            var mode = RenderSettings.defaultReflectionMode;
            RenderSettings.defaultReflectionMode = mode == DefaultReflectionMode.Custom ? DefaultReflectionMode.Skybox : DefaultReflectionMode.Custom;
            RenderSettings.defaultReflectionMode = mode;
            DynamicGI.UpdateEnvironment();
        }

        public static bool Write(Camera camera, string path, int width, int height, Report report)
        {
            if (camera == null) return false;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.antiAliasing = 4;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D picture = null;
            bool asyncCompilation = UnityEditor.ShaderUtil.allowAsyncCompilation;
            try
            {
                // every shader variant must be ready, else the editor draws a placeholder colour for the ones still
                // compiling (the first batch-mode render showed flat salmon surfaces that way)
                UnityEditor.ShaderUtil.allowAsyncCompilation = false;
                RefreshReflections();
                camera.targetTexture = rt;
                camera.Render();
                camera.Render();
                camera.Render();
                RenderTexture.active = rt;
                picture = new Texture2D(width, height, TextureFormat.RGB24, false);
                picture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                picture.Apply();
                File.WriteAllBytes(path, picture.EncodeToPNG());
                report.Notes.Add("Unity's view from " + camera.name + " written as " + Path.GetFileName(path) + " (" + width + " x " + height + ")");
                return true;
            }
            catch (System.Exception e)
            {
                report.Warn("No Unity screenshot: " + e.Message);
                return false;
            }
            finally
            {
                UnityEditor.ShaderUtil.allowAsyncCompilation = asyncCompilation;
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (picture != null) Object.DestroyImmediate(picture);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
