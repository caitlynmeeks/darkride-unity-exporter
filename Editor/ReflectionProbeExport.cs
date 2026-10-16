// The baked reflection probe as the zone's ambient map: the engine reads a cube map laid out as a vertical strip
// (1:6, faces +X -X +Y -Y +Z -Z from the top), so the probe's cubemap is resampled into that strip on the GPU,
// direction by direction, with the world's X mirror and the engine's face conventions in the shader.

using System.IO;
using UnityEditor;
using UnityEngine;

namespace Darkride.Exporter
{
    public static class ReflectionProbeExport
    {
        const string SHADER_PATH = "Packages/com.darkride.exporter/Editor/Shaders/DarkrideCubemapStrip.shader";
        const int MAX_FACE = 1024;

        public static string Write(ReflectionProbe probe, string probesDir, string fileName, Report report)
        {
            var cube = probe.bakedTexture;
            if (cube == null)
            {
                report.Warn("Reflection probe " + probe.name + " has no baked texture");
                return null;
            }
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(SHADER_PATH);
            if (shader == null) shader = Shader.Find("Hidden/Darkride/CubemapStrip");
            if (shader == null)
            {
                report.Warn("The cubemap strip shader was not found at " + SHADER_PATH + "; no reflection probe written");
                return null;
            }
            int size = Mathf.Clamp(cube.width, 16, MAX_FACE);
            var material = new Material(shader);
            material.SetTexture("_Cube", cube);
            material.SetFloat("_FaceSize", size);
            int height = size * 6;
            var rt = RenderTexture.GetTemporary(size, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Graphics.Blit(Texture2D.blackTexture, rt, material);
            RenderTexture.active = rt;
            var copy = new Texture2D(size, height, TextureFormat.RGBAHalf, false, true);
            copy.ReadPixels(new Rect(0, 0, size, height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var image = GpuReadback.FromPixels(copy.GetPixels(), size, height);
            Object.DestroyImmediate(copy);
            Object.DestroyImmediate(material);
            Exr.WriteHalfRgb(Path.Combine(probesDir, fileName), image);
            report.Notes.Add("Reflection probe " + probe.name + " (" + cube.width + " a face" + (cube is Cubemap c && IsCompressed(c.format) ? ", read from the imported " + c.format + " texture" : "") + ") written as " + fileName + ", " + size + " x " + height);
            return fileName;
        }

        static bool IsCompressed(TextureFormat format)
        {
            switch (format)
            {
                case TextureFormat.BC6H:
                case TextureFormat.BC7:
                case TextureFormat.DXT1:
                case TextureFormat.DXT5:
                case TextureFormat.ASTC_4x4:
                case TextureFormat.ASTC_6x6:
                case TextureFormat.ASTC_8x8:
                    return true;
            }
            return false;
        }
    }
}
