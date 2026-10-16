// The baked lightmaps: Unity's Lightmap-N_comp_light.exr (float RGBA, ZIP) re-encoded as half RGB ZIP, read from the
// source file so that the import settings (compression, size) play no part; and Lightmap-N_comp_dir.png, the
// dominant direction, with its x mirrored like the world. When a source file cannot be read (another format, or a
// compression the reader lacks), the imported texture is read back from the GPU instead, and the report says so.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Darkride.Exporter
{
    public static class LightmapExport
    {
        public class Written
        {
            public int Index;
            public string ColorFile;
            public string DirectionFile;
        }

        public static Written Write(int index, string lightmapsDir, Report report)
        {
            var lightmap = LightmapSettings.lightmaps[index];
            var written = new Written { Index = index };

            if (lightmap.lightmapColor != null)
            {
                string full = FullPath(lightmap.lightmapColor);
                FloatImage image = null;
                if (full != null && full.EndsWith(".exr", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        image = Exr.Read(full);
                    }
                    catch (Exception e)
                    {
                        report.Warn("Lightmap " + index + ": " + e.Message + "; the imported texture was read instead (its import settings apply)");
                    }
                }
                else
                {
                    report.Warn("Lightmap " + index + " is not an EXR file (" + (full ?? "no asset path") + "); the imported texture was read instead");
                }
                if (image == null) image = GpuReadback.ReadFloat(lightmap.lightmapColor);
                written.ColorFile = "Lightmap-" + index + ".exr";
                Exr.WriteHalfRgb(Path.Combine(lightmapsDir, written.ColorFile), image);
                report.Count("lightmaps written (half RGB, ZIP)");
                report.Notes.Add("Lightmap " + index + ": " + image.Width + " x " + image.Height + (full != null && full.EndsWith(".exr", StringComparison.OrdinalIgnoreCase) ? ", from " + Path.GetFileName(full) : ""));
            }

            if (lightmap.lightmapDir != null)
            {
                string full = FullPath(lightmap.lightmapDir);
                Color32[] pixels = null;
                int width = 0, height = 0;
                Texture2D loaded = null;
                if (full != null && full.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                    if (loaded.LoadImage(File.ReadAllBytes(full), false))
                    {
                        pixels = loaded.GetPixels32();
                        width = loaded.width;
                        height = loaded.height;
                    }
                }
                if (pixels == null)
                {
                    report.Warn("Direction map " + index + ": the source PNG could not be read; the imported texture was read instead");
                    pixels = GpuReadback.ReadBytes(lightmap.lightmapDir, out width, out height);
                }
                for (int i = 0; i < pixels.Length; i++) pixels[i].r = (byte)(255 - pixels[i].r);
                var output = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                output.SetPixels32(pixels);
                written.DirectionFile = "Lightmap-" + index + "_dir.png";
                File.WriteAllBytes(Path.Combine(lightmapsDir, written.DirectionFile), output.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(output);
                if (loaded != null) UnityEngine.Object.DestroyImmediate(loaded);
                report.Count("direction maps written");
            }
            return written;
        }

        public static string FullPath(UnityEngine.Object asset)
        {
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath)) return null;
            if (Path.IsPathRooted(assetPath)) return assetPath;
            string project = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(project, assetPath));
        }
    }

    // Reads a texture's pixels through a render texture, for textures that are not readable or not on disk.
    public static class GpuReadback
    {
        public static FloatImage ReadFloat(Texture texture)
        {
            int width = texture.width, height = texture.height;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var image = FromPixels(copy.GetPixels(), width, height);
            UnityEngine.Object.DestroyImmediate(copy);
            return image;
        }

        public static Color32[] ReadBytes(Texture texture, out int width, out int height)
        {
            width = texture.width;
            height = texture.height;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
            var pixels = copy.GetPixels32();
            UnityEngine.Object.DestroyImmediate(copy);
            return pixels;
        }

        // Unity's pixel rows run bottom to top; the image's top to bottom
        public static FloatImage FromPixels(Color[] pixels, int width, int height)
        {
            var image = new FloatImage(width, height);
            for (int y = 0; y < height; y++)
            {
                int sourceRow = (height - 1 - y) * width;
                int targetRow = y * width * 3;
                for (int x = 0; x < width; x++)
                {
                    var c = pixels[sourceRow + x];
                    image.Rgb[targetRow + x * 3] = c.r;
                    image.Rgb[targetRow + x * 3 + 1] = c.g;
                    image.Rgb[targetRow + x * 3 + 2] = c.b;
                }
            }
            return image;
        }
    }
}
