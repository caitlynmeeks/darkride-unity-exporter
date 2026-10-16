// A picture of the scene from its camera, rendered by Unity as it is, for the side by side with darkride
// (LIGHTMAPS.md 3.9): the camera's post-processing applies, so what Unity shows is what the file holds.

using System.IO;
using UnityEngine;

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

        public static bool Write(Camera camera, string path, int width, int height, Report report)
        {
            if (camera == null) return false;
            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            rt.antiAliasing = 4;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D picture = null;
            try
            {
                camera.targetTexture = rt;
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
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (picture != null) Object.DestroyImmediate(picture);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
