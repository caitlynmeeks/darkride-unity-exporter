// Why does Unity draw a surface the way it does under its reflection probes? For the side by side with darkride
// (LIGHTMAPS.md, the probe test): the render pipeline in force and its probe settings, the scene's probes and their
// textures, what each renderer with a watched material gets from the probes, and the scene camera's view three ways:
// as it is, with every probe's box projection off, and with every probe disabled. Batch mode:
//   Unity -batchmode -projectPath <project> -executeMethod Darkride.Exporter.DarkrideCli.ExportScene
//         -darkrideScene Assets/Scenes/Room.unity -darkrideOut /path/to/exports -darkrideProbeDiagnostic
//         [-darkrideWatch Chrome,Green] -logFile diag.log
// Nothing is saved: the probes are put back as they were.

using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Darkride.Exporter
{
    public static class ProbeDiagnostic
    {
        public static void Run(string dir, string[] watchedMaterials)
        {
            var sb = new StringBuilder();
            var rp = GraphicsSettings.currentRenderPipeline;
            int level = QualitySettings.GetQualityLevel();
            sb.AppendLine("render pipeline asset in force: " + (rp ? rp.name : "none") + "; quality level " + level + " (" + QualitySettings.names[level] + ")" +
                          "; the quality level's own asset: " + (QualitySettings.renderPipeline ? QualitySettings.renderPipeline.name : "none"));
            if (rp != null)
            {
                // the URP asset's fields by name: this package does not reference the URP assembly
                var so = new SerializedObject(rp);
                sb.AppendLine("pipeline asset: reflectionProbeBlending " + Prop(so, "m_ReflectionProbeBlending") + ", reflectionProbeBoxProjection " +
                              Prop(so, "m_ReflectionProbeBoxProjection") + ", supportsHDR " + Prop(so, "m_SupportsHDR"));
                var list = so.FindProperty("m_RendererDataList");
                if (list != null)
                {
                    for (int i = 0; i < list.arraySize; i++)
                    {
                        var data = list.GetArrayElementAtIndex(i).objectReferenceValue;
                        if (data == null) continue;
                        var dso = new SerializedObject(data);
                        sb.AppendLine("  renderer data " + i + ": " + data.name + ", m_RenderingMode " + Prop(dso, "m_RenderingMode") +
                                      " (0 forward, 1 deferred, 2 forward+, 3 deferred+)");
                    }
                }
            }
            sb.AppendLine("RenderSettings: ambientMode " + RenderSettings.ambientMode + ", ambientLight " + RenderSettings.ambientLight +
                          ", defaultReflectionMode " + RenderSettings.defaultReflectionMode + ", reflectionIntensity " + RenderSettings.reflectionIntensity +
                          ", customReflection " + (RenderSettings.customReflectionTexture ? RenderSettings.customReflectionTexture.name : "none") +
                          ", skybox " + (RenderSettings.skybox ? RenderSettings.skybox.name : "none"));

            var probes = Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None);
            foreach (var p in probes)
            {
                var baked = p.bakedTexture;
                sb.AppendLine("probe " + HierarchyPath(p.transform) + ": enabled " + p.enabled + ", mode " + p.mode + ", importance " + p.importance +
                              ", intensity " + p.intensity + ", boxProjection " + p.boxProjection + ", size " + p.size + ", center " + p.center +
                              ", blendDistance " + p.blendDistance + ", position " + p.transform.position +
                              ", texture " + (p.texture ? p.texture.name + " " + p.texture.width : "none") +
                              ", bakedTexture " + (baked ? baked.name + " " + baked.width + " " + (baked is Cubemap c ? c.format.ToString() : baked.GetType().Name) : "none") +
                              ", hdrDecode " + p.textureHDRDecodeValues);
            }

            var watched = new HashSet<string>(watchedMaterials);
            var blend = new List<ReflectionProbeBlendInfo>();
            int shown = 0;
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                string names = "";
                bool hit = false;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    names += m.name + " ";
                    if (watched.Contains(m.name)) hit = true;
                }
                if (!hit) continue;
                r.GetClosestReflectionProbes(blend);
                string got = "";
                foreach (var b in blend) got += (b.probe ? b.probe.name : "null") + " weight " + b.weight.ToString("0.00") + "; ";
                sb.AppendLine("renderer " + HierarchyPath(r.transform) + " [" + names.Trim() + "]: reflectionProbeUsage " + r.reflectionProbeUsage +
                              ", lightProbeUsage " + r.lightProbeUsage + ", lightmapIndex " + r.lightmapIndex + ", renderingLayerMask " + r.renderingLayerMask +
                              ", reflectionProbeStatic " + GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.ReflectionProbeStatic) +
                              ", bounds centre " + r.bounds.center + ", probes: " + (got.Length > 0 ? got : "none"));
                if (++shown >= 16) { sb.AppendLine("  (more renderers with these materials not listed)"); break; }
            }
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (!watched.Contains(m.name) || !AssetDatabase.Contains(m)) continue;
                sb.AppendLine("material " + m.name + ": shader " + m.shader.name + ", keywords [" + string.Join(" ", m.shaderKeywords) + "]" +
                              (m.HasProperty("_Metallic") ? ", _Metallic " + m.GetFloat("_Metallic") : "") +
                              (m.HasProperty("_Smoothness") ? ", _Smoothness " + m.GetFloat("_Smoothness") : "") +
                              (m.HasProperty("_EnvironmentReflections") ? ", _EnvironmentReflections " + m.GetFloat("_EnvironmentReflections") : "") +
                              (m.HasProperty("_BaseColor") ? ", _BaseColor " + m.GetColor("_BaseColor") : ""));
            }

            Screenshot.RefreshReflections();
            var camera = Screenshot.SceneCamera();
            var report = new Report();
            Screenshot.Write(camera, Path.Combine(dir, "unity-diag-base.png"), 1920, 1080, report);
            var savedBox = new Dictionary<ReflectionProbe, bool>();
            foreach (var p in probes) { savedBox[p] = p.boxProjection; p.boxProjection = false; }
            Screenshot.Write(camera, Path.Combine(dir, "unity-diag-boxproj-off.png"), 1920, 1080, report);
            foreach (var p in probes) { p.boxProjection = savedBox[p]; p.enabled = false; }
            Screenshot.Write(camera, Path.Combine(dir, "unity-diag-probe-off.png"), 1920, 1080, report);
            foreach (var p in probes) p.enabled = true;

            // which path the picture reflects: a solid red cube map as every probe's custom texture, then as the
            // environment's fallback reflection; whatever turns red reflects that path
            var red = new Cubemap(16, TextureFormat.RGBAHalf, false);
            var pixels = new Color[16 * 16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(1.0f, 0.0f, 0.0f, 1.0f);
            for (int face = 0; face < 6; face++) red.SetPixels(pixels, (CubemapFace)face);
            red.Apply();
            var savedMode = new Dictionary<ReflectionProbe, ReflectionProbeMode>();
            foreach (var p in probes) { savedMode[p] = p.mode; p.mode = ReflectionProbeMode.Custom; p.customBakedTexture = red; }
            Screenshot.Write(camera, Path.Combine(dir, "unity-diag-probe-red.png"), 1920, 1080, report);
            foreach (var p in probes) { p.customBakedTexture = null; p.mode = savedMode[p]; }
            var savedReflectionMode = RenderSettings.defaultReflectionMode;
            var savedCustom = RenderSettings.customReflectionTexture;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = red;
            Screenshot.Write(camera, Path.Combine(dir, "unity-diag-fallback-red.png"), 1920, 1080, report);
            RenderSettings.customReflectionTexture = savedCustom;
            RenderSettings.defaultReflectionMode = savedReflectionMode;
            Object.DestroyImmediate(red);
            sb.AppendLine(report.Text());

            File.WriteAllText(Path.Combine(dir, "probe-diagnostic.txt"), sb.ToString());
            Debug.Log("[darkride] probe diagnostic\n" + sb);
        }

        static string Prop(SerializedObject so, string name)
        {
            var p = so.FindProperty(name);
            if (p == null) return "?";
            switch (p.propertyType)
            {
                case SerializedPropertyType.Boolean: return p.boolValue.ToString();
                case SerializedPropertyType.Integer: return p.intValue.ToString();
                case SerializedPropertyType.Enum: return p.enumValueIndex.ToString();
                default: return p.propertyType.ToString();
            }
        }

        static string HierarchyPath(Transform t)
        {
            return t.parent ? HierarchyPath(t.parent) + "/" + t.name : t.name;
        }
    }
}
