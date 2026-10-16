// One glTF per distinct mesh and material set, written by UnityGLTF from a temporary object at the origin (the
// entity carries the placement): meshes with Unity's second UV set as TEXCOORD_1 (v flipped), URP/Lit materials as
// glTF PBR (metallic-smoothness converted to metallic-roughness, KHR_texture_transform for tiling,
// KHR_materials_emissive_strength for HDR emission), textures copied from their source files where they are PNG or
// JPEG. UnityGLTF writes textures beside the .gltf under their own names; they are moved into models/textures/
// under one name per source texture, so every model shares them and a re-export keeps the names.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GLTF.Schema;
using UnityEditor;
using UnityEngine;
using UnityGLTF;
using UnityGLTF.Plugins;

namespace Darkride.Exporter
{
    public class MeshExport
    {
        readonly string modelsDir;
        readonly Report report;
        readonly Dictionary<string, string> files = new Dictionary<string, string>();
        readonly HashSet<string> usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> textureFiles = new Dictionary<string, string>();
        readonly HashSet<string> usedTextureFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int MeshesWritten;
        public int TexturesWritten => textureFiles.Count;

        public MeshExport(string modelsDir, Report report)
        {
            this.modelsDir = modelsDir;
            this.report = report;
        }

        // The file name (without .gltf) of the model for this entity's mesh and materials, written on first use
        public string FileFor(ExportEntity e)
        {
            var key = new StringBuilder(e.Mesh.GetInstanceID().ToString());
            foreach (var material in e.Materials) key.Append('|').Append(material != null ? material.GetInstanceID() : 0);
            if (files.TryGetValue(key.ToString(), out string existing)) return existing;

            string baseName = BaseName(e.Mesh);
            string name = baseName;
            for (int i = 2; usedFiles.Contains(name); i++) name = baseName + "-" + i;
            usedFiles.Add(name);
            Export(e, name);
            files[key.ToString()] = name;
            return name;
        }

        static string BaseName(Mesh mesh)
        {
            string asset = AssetDatabase.GetAssetPath(mesh);
            string assetName = string.IsNullOrEmpty(asset) ? "" : Path.GetFileNameWithoutExtension(asset);
            string meshName = mesh.name ?? "";
            string name;
            if (assetName.Length == 0) name = meshName;
            else if (meshName.Length == 0 || assetName == meshName) name = assetName;
            else name = assetName + "-" + meshName;
            return ExportOptions.SafeName(name);
        }

        void Export(ExportEntity e, string name)
        {
            var go = new GameObject(name);
            go.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = e.Mesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = e.Materials;

                var settings = GLTFSettings.GetDefaultSettings();
                settings.ExportNames = true;
                settings.ExportFullPath = false;
                settings.TryExportTexturesFromDisk = true;
                settings.UseTextureFileTypeHeuristic = false;
                settings.ExportVertexColors = true;
                settings.ExportDisabledGameObjects = false;
                settings.ExportAnimations = false;
                settings.BakeSkinnedMeshes = false;
                settings.UseMainCameraVisibility = false;
                settings.UseCaching = false;
                foreach (var plugin in settings.ExportPlugins)
                {
                    if (plugin is MaterialExtensionsExport materials)
                    {
                        materials.Enabled = true;
                        materials.KHR_materials_emissive_strength = true;
                    }
                }

                var imageTextures = new Dictionary<int, GLTFSceneExporter.UniqueTexture>();
                GLTFRoot root = null;
                var context = new ExportContext(settings)
                {
                    TexturePathRetriever = texture => ExportOptions.SafeName(texture.name),
                    AfterTextureExport = (exporter, texture, index, gltfTexture) =>
                    {
                        if (gltfTexture != null && gltfTexture.Source != null) imageTextures[gltfTexture.Source.Id] = texture;
                    },
                    AfterSceneExport = (exporter, gltfRoot) => root = gltfRoot,
                };
                var sceneExporter = new GLTFSceneExporter(new[] { go.transform }, context);
                sceneExporter.SaveGLTFandBin(modelsDir, name, true, false);
                MeshesWritten++;
                if (root != null) MoveTextures(name, root, imageTextures);
                else report.Warn(name + ": UnityGLTF gave no document back; its textures stay beside the model");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        void MoveTextures(string modelName, GLTFRoot root, Dictionary<int, GLTFSceneExporter.UniqueTexture> imageTextures)
        {
            if (root.Images == null || root.Images.Count == 0) return;
            string gltfPath = Path.Combine(modelsDir, modelName + ".gltf");
            string json = File.ReadAllText(gltfPath);
            bool changed = false;
            for (int i = 0; i < root.Images.Count; i++)
            {
                string uri = root.Images[i].Uri;
                if (string.IsNullOrEmpty(uri) || uri.StartsWith("data:")) continue;
                string relative = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
                string source = Path.Combine(modelsDir, relative);
                if (!File.Exists(source))
                {
                    report.Warn(modelName + ": texture " + uri + " was not written");
                    continue;
                }
                imageTextures.TryGetValue(i, out var unique);
                string fileName = TextureFileName(unique, Path.GetFileName(relative));
                string target = Path.Combine(modelsDir, "textures", fileName);
                if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(source, target, true);
                    File.Delete(source);
                }
                string newUri = "textures/" + Uri.EscapeDataString(fileName);
                if (newUri == uri) continue;
                var pattern = new Regex("\"uri\"\\s*:\\s*\"" + Regex.Escape(uri) + "\"");
                int replaced = 0;
                json = pattern.Replace(json, match => { replaced++; return "\"uri\":\"" + newUri + "\""; }, 1);
                if (replaced == 0) report.Warn(modelName + ": could not point the model at " + newUri + " (uri " + uri + " not found in the file)");
                else changed = true;
            }
            if (changed) File.WriteAllText(gltfPath, json);
        }

        // One file name per source texture and conversion, the same in every model of the export
        string TextureFileName(GLTFSceneExporter.UniqueTexture unique, string writtenName)
        {
            string extension = Path.GetExtension(writtenName);
            if (string.IsNullOrEmpty(extension)) extension = ".png";
            string key, baseName;
            if (unique.Texture != null)
            {
                string asset = AssetDatabase.GetAssetPath(unique.Texture);
                string source = string.IsNullOrEmpty(asset) ? "id:" + unique.Texture.GetInstanceID() : asset;
                string conversion = unique.ExportSettings.conversion.ToString();
                string suffix = conversion == "None" ? "" : "-" + ConversionName(conversion);
                key = source + "|" + conversion + "|" + unique.ExportSettings.linear + "|" + unique.ExportSettings.alphaMode + "|" + extension.ToLowerInvariant();
                string sourceName = string.IsNullOrEmpty(asset) ? unique.Texture.name : Path.GetFileNameWithoutExtension(asset);
                baseName = ExportOptions.SafeName(sourceName + suffix);
            }
            else
            {
                key = "written:" + writtenName;
                baseName = ExportOptions.SafeName(Path.GetFileNameWithoutExtension(writtenName));
            }
            if (textureFiles.TryGetValue(key, out string known)) return known;
            string candidate = baseName + extension;
            for (int i = 2; usedTextureFiles.Contains(candidate); i++) candidate = baseName + "-" + i + extension;
            usedTextureFiles.Add(candidate);
            textureFiles[key] = candidate;
            return candidate;
        }

        static string ConversionName(string conversion)
        {
            switch (conversion)
            {
                case "MetalGlossChannelSwap": return "metallicRoughness";
                case "MetalGlossOcclusionChannelSwap": return "metallicRoughnessOcclusion";
                case "NormalChannel": return "normal";
                default: return conversion.ToLowerInvariant();
            }
        }
    }
}
