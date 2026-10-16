// One export: collect the scene, write one glTF per distinct mesh and material set, re-encode the lightmaps, write
// the reflection probe strip, make the zone, write scene.json in the engine's entity format with every link on the
// folder's base URL, write report.txt, and upload the folder into My Files. Every entity gets userData "export":
// the folder name, so a script can find and remove an import.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Darkride.Exporter
{
    public class ExportResult
    {
        public string Folder;
        public string LocalPath;
        public string BaseUrl;
        public string SceneUrl;
        public string ImportScriptUrl;
        public int EntityCount;
        public Report Report;
    }

    public static class SceneExport
    {
        // libraries/networking/src/udt/PacketHeaders.h, EntityVersion::LAST_PACKET_TYPE on native-editor (TrackEntities):
        // the reader only compares it against old content migrations, so any current value does
        const int ENTITY_VERSION = 135;

        public static ExportResult Run(ExportOptions options, MetaverseClient client, Action<string, float> progress)
        {
            progress = progress ?? ((step, fraction) => { });
            var report = new Report();
            var scene = SceneManager.GetActiveScene();
            string folder = string.IsNullOrEmpty(options.FolderName) ? ExportOptions.DefaultFolderName(scene.name) : ExportOptions.SafeName(options.FolderName);
            string outputRoot = string.IsNullOrEmpty(options.OutputRoot) ? ExportOptions.DefaultOutputRoot() : options.OutputRoot;
            string localPath = Path.Combine(outputRoot, folder);

            string baseUrl;
            if (options.Upload)
            {
                if (client == null || !client.LoggedIn) throw new InvalidOperationException("Log in first, or turn Upload off to write the folder locally.");
                progress("Checking the account…", 0);
                baseUrl = client.FilesBase() + "/" + Uri.EscapeDataString(folder);
            }
            else
            {
                baseUrl = new Uri(localPath).AbsoluteUri.TrimEnd('/');
            }

            // written into a staging folder first: a failed export leaves the previous one in place
            string finalPath = localPath;
            localPath = finalPath + ".exporting";
            if (Directory.Exists(localPath)) Directory.Delete(localPath, true);
            string modelsDir = Path.Combine(localPath, "models");
            string lightmapsDir = Path.Combine(localPath, "lightmaps");
            string probesDir = Path.Combine(localPath, "probes");
            Directory.CreateDirectory(Path.Combine(modelsDir, "textures"));
            Directory.CreateDirectory(lightmapsDir);
            Directory.CreateDirectory(probesDir);

            report.Notes.Add("Darkride export of " + scene.name + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            report.Notes.Add("Folder: " + localPath);
            report.Notes.Add("Links: " + baseUrl);

            progress("Collecting the scene…", 0.02f);
            var collector = new SceneCollector(options, report);
            IEnumerable<GameObject> roots = options.Roots != null && options.Roots.Length > 0 ? TopMost(options.Roots) : scene.GetRootGameObjects();
            collector.Collect(roots);
            var entities = collector.Entities;
            if (entities.Count == 0) throw new InvalidOperationException("Nothing to export: no active MeshRenderer with a mesh, and no Mixed or Realtime light.");

            var meshes = new MeshExport(modelsDir, report);
            var models = entities.Where(e => e.Type == "Model" && e.Mesh != null).ToList();
            for (int i = 0; i < models.Count; i++)
            {
                var e = models[i];
                progress("Meshes " + (i + 1) + " of " + models.Count + ": " + e.Name, 0.05f + 0.55f * i / Math.Max(1, models.Count));
                string file = meshes.FileFor(e);
                e.Properties["modelURL"] = baseUrl + "/models/" + Uri.EscapeDataString(file + ".gltf");
            }
            report.Notes.Add(meshes.MeshesWritten + " glTF files, " + meshes.TexturesWritten + " textures");

            var lightmaps = new Dictionary<int, LightmapExport.Written>();
            if (options.ExportLightmaps)
            {
                var indices = collector.LightmapIndices.OrderBy(i => i).ToList();
                for (int i = 0; i < indices.Count; i++)
                {
                    progress("Lightmap " + indices[i], 0.6f + 0.2f * i / Math.Max(1, indices.Count));
                    lightmaps[indices[i]] = LightmapExport.Write(indices[i], lightmapsDir, report);
                }
                foreach (var e in models)
                {
                    if (e.LightmapIndex < 0 || !lightmaps.TryGetValue(e.LightmapIndex, out var written) || written.ColorFile == null) continue;
                    var so = Conversions.LightmapScaleOffset(e.LightmapScaleOffset);
                    var entry = new Dictionary<string, object>
                    {
                        { "url", baseUrl + "/lightmaps/" + written.ColorFile },
                        { "scaleOffset", new List<object> { so.x, so.y, so.z, so.w } },
                        { "mode", "indirect" },
                    };
                    if (written.DirectionFile != null) entry["directionalUrl"] = baseUrl + "/lightmaps/" + written.DirectionFile;
                    e.UserData["lightmap"] = entry;
                }
            }

            // every baked reflection probe becomes a reflection probe entity (an invisible box the probe's size, with
            // its cube map strip), and the largest one is the zone's ambient map as well, for whatever lies outside
            // every probe
            string ambientUrl = null;
            float ambientIntensity = 1;
            if (options.ExportReflectionProbe)
            {
                var probes = BakedProbes(roots);
                if (probes.Count == 0)
                {
                    report.Notes.Add("No baked reflection probe: the zone keeps the engine's default ambient");
                }
                var usedNames = new HashSet<string>();
                for (int i = 0; i < probes.Count; i++)
                {
                    var probe = probes[i];
                    progress("Reflection probe " + probe.name, 0.82f + 0.05f * i / Math.Max(1, probes.Count));
                    string fileName = ProbeFileName(probe, usedNames);
                    string file = ReflectionProbeExport.Write(probe, probesDir, fileName, report);
                    if (file == null) continue;
                    string url = baseUrl + "/probes/" + file;
                    if (ambientUrl == null)
                    {
                        ambientUrl = url;
                        ambientIntensity = probe.intensity;
                    }
                    entities.Add(MakeProbe(probe, url));
                }
                if (probes.Count > 0) report.Count("reflection probes (entities; the largest is the zone's ambient map too)", probes.Count);
            }

            ExportEntity zone = MakeZone(collector, scene.name, ambientUrl, ambientIntensity, report);
            if (options.ExportZone) entities.Add(zone);

            progress("Writing scene.json", 0.88f);
            var list = new List<object>();
            foreach (var e in entities)
            {
                e.UserData["export"] = folder;
                list.Add(ToProperties(e));
            }
            var document = new Dictionary<string, object>
            {
                { "Version", ENTITY_VERSION },
                { "Entities", list },
            };
            File.WriteAllText(Path.Combine(localPath, "scene.json"), Json.Serialize(document, true));
            report.Notes.Add(entities.Count + " entities in scene.json");

            var camera = Screenshot.SceneCamera();
            ScriptsExport.Write(localPath, baseUrl, folder, camera, zone.Position);
            if (camera != null && options.Screenshot)
            {
                progress("Unity's view", 0.89f);
                Screenshot.Write(camera, Path.Combine(localPath, "unity-view.png"), 1920, 1080, report);
            }

            var result = new ExportResult
            {
                Folder = folder,
                LocalPath = localPath,
                BaseUrl = baseUrl,
                SceneUrl = baseUrl + "/scene.json",
                ImportScriptUrl = baseUrl + "/import-scene.js",
                EntityCount = entities.Count,
                Report = report,
            };

            if (options.Upload)
            {
                UploadFolder(client, folder, localPath, progress, report);
            }
            File.WriteAllText(Path.Combine(localPath, "report.txt"), report.Text());
            if (Directory.Exists(finalPath)) Directory.Delete(finalPath, true);
            Directory.Move(localPath, finalPath);
            result.LocalPath = finalPath;
            return result;
        }

        // The selected objects without those inside other selected objects, so nothing is exported twice
        static IEnumerable<GameObject> TopMost(GameObject[] selected)
        {
            var set = new HashSet<Transform>(selected.Where(g => g != null).Select(g => g.transform));
            foreach (var t in set)
            {
                bool inside = false;
                for (var p = t.parent; p != null && !inside; p = p.parent) inside = set.Contains(p);
                if (!inside) yield return t.gameObject;
            }
        }

        // The scene's baked reflection probes (under the selected roots, for a selection), the largest first
        static List<ReflectionProbe> BakedProbes(IEnumerable<GameObject> roots)
        {
            var rootSet = new HashSet<Transform>(roots.Select(g => g.transform));
            return UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None)
                .Where(p => p.enabled && p.gameObject.activeInHierarchy && p.mode == UnityEngine.Rendering.ReflectionProbeMode.Baked && p.bakedTexture != null)
                .Where(p => UnderOneOf(p.transform, rootSet))
                .OrderByDescending(p => p.size.x * p.size.y * p.size.z)
                .ToList();
        }

        static bool UnderOneOf(Transform t, HashSet<Transform> roots)
        {
            for (var p = t; p != null; p = p.parent) if (roots.Contains(p)) return true;
            return false;
        }

        static string ProbeFileName(ReflectionProbe probe, HashSet<string> used)
        {
            string stem = System.Text.RegularExpressions.Regex.Replace(probe.name, "[^A-Za-z0-9._-]+", "_").Trim('_');
            if (stem.Length == 0) stem = "ReflectionProbe";
            string name = stem;
            for (int n = 2; used.Contains(name); n++) name = stem + "-" + n;
            used.Add(name);
            return name + ".exr";
        }

        // A reflection probe entity (LIGHTMAPS.md §7): an invisible, collisionless box the size of Unity's box, at the
        // probe's position, unparented since Unity's box is axis-aligned in the world whatever the transform; the
        // engine reads the entry "reflectionProbe" of its userData. Unity's center (the box's offset from the
        // probe) mirrors X like every point
        static ExportEntity MakeProbe(ReflectionProbe probe, string url)
        {
            var e = new ExportEntity { Type = "Shape", Name = probe.name, Source = probe.transform };
            e.Position = Conversions.Point(probe.transform.position);
            e.Rotation = Quaternion.identity;
            e.Dimensions = Conversions.Size(probe.size);
            e.Properties["shape"] = "Cube";
            e.Properties["visible"] = false;
            e.Properties["collisionless"] = true;
            var center = Conversions.Direction(probe.center);
            e.UserData["reflectionProbe"] = new Dictionary<string, object>
            {
                { "url", url },
                { "boxOffset", new List<object> { center.x, center.y, center.z } },
                { "intensity", probe.intensity },
                { "importance", probe.importance },
                { "blendDistance", probe.blendDistance },
                { "boxProjection", probe.boxProjection },
            };
            return e;
        }

        static ExportEntity MakeZone(SceneCollector collector, string sceneName, string ambientUrl, float ambientIntensity, Report report)
        {
            var e = new ExportEntity { Type = "Zone", Name = sceneName + " zone" };
            Bounds bounds = collector.WorldBounds ?? new Bounds(Vector3.zero, Vector3.one * 10);
            e.Position = Conversions.Point(bounds.center);
            e.Rotation = Quaternion.identity;
            e.Dimensions = Vector3.Max(bounds.size + Vector3.one * 4, Vector3.one * 10);
            var p = e.Properties;
            p["shapeType"] = "box";

            var sun = collector.DirectionalLights.FirstOrDefault();
            if (sun != null)
            {
                p["keyLightMode"] = "enabled";
                p["keyLight"] = new Dictionary<string, object>
                {
                    { "color", (Color32)sun.color },
                    { "intensity", sun.intensity },
                    { "direction", Conversions.Direction(sun.transform.forward) },
                    { "castShadows", sun.shadows != LightShadows.None },
                };
                report.Notes.Add("Key light: " + sun.lightmapBakeType + " directional light " + SceneCollector.PathOf(sun.transform));
                if (collector.DirectionalLights.Count > 1)
                    report.Warn("More than one Mixed or Realtime directional light; only " + sun.name + " became the key light");
            }
            else
            {
                p["keyLightMode"] = "disabled";
                report.Notes.Add("Key light off: no Mixed or Realtime directional light (a baked-only scene)");
            }

            if (ambientUrl != null)
            {
                p["ambientLightMode"] = "enabled";
                p["ambientLight"] = new Dictionary<string, object>
                {
                    { "ambientURL", ambientUrl },
                    { "ambientIntensity", ambientIntensity },
                };
            }
            else
            {
                p["ambientLightMode"] = "inherit";
            }

            switch (RenderSettings.ambientMode)
            {
                case UnityEngine.Rendering.AmbientMode.Flat:
                    p["skyboxMode"] = "enabled";
                    p["skybox"] = new Dictionary<string, object> { { "color", Conversions.ColorBytes(RenderSettings.ambientLight) } };
                    break;
                case UnityEngine.Rendering.AmbientMode.Trilight:
                    p["skyboxMode"] = "enabled";
                    p["skybox"] = new Dictionary<string, object> { { "color", Conversions.ColorBytes(RenderSettings.ambientSkyColor) } };
                    break;
                default:
                    p["skyboxMode"] = "inherit";
                    if (RenderSettings.skybox != null) report.Notes.Add("The skybox material " + RenderSettings.skybox.name + " is not exported yet; the zone inherits its skybox");
                    break;
            }
            p["hazeMode"] = "disabled";
            p["bloomMode"] = "inherit";
            p["flyingAllowed"] = true;
            p["ghostingAllowed"] = true;
            return e;
        }

        static Dictionary<string, object> ToProperties(ExportEntity e)
        {
            var p = new Dictionary<string, object>
            {
                { "id", e.Id },
                { "type", e.Type },
                { "name", e.Name },
            };
            // in the entity format, position and rotation are relative to the parent when there is one (the engine's
            // own export writes them so, and its import ignores localPosition and localRotation)
            if (e.Parent != null)
            {
                p["parentID"] = e.Parent.Id;
                var inverse = Quaternion.Inverse(e.Parent.Rotation);
                p["position"] = inverse * (e.Position - e.Parent.Position);
                p["rotation"] = inverse * e.Rotation;
            }
            else
            {
                p["position"] = e.Position;
                p["rotation"] = e.Rotation;
            }
            p["dimensions"] = e.Dimensions;
            foreach (var kv in e.Properties) p[kv.Key] = kv.Value;
            if (e.UserData.Count > 0) p["userData"] = Json.Serialize(e.UserData);
            return p;
        }

        static void UploadFolder(MetaverseClient client, string folder, string localPath, Action<string, float> progress, Report report)
        {
            progress("Creating folders in My Files", 0.9f);
            foreach (string sub in new[] { "", "/models", "/models/textures", "/lightmaps", "/probes" })
                client.CreateFolder("/" + folder + sub);

            var files = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("."))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            // scene.json last, so a world refreshing it never sees links to files not yet there
            string sceneJson = Path.Combine(localPath, "scene.json");
            files.Remove(sceneJson);
            files.Add(sceneJson);
            long total = 0;
            for (int i = 0; i < files.Count; i++)
            {
                string relative = files[i].Substring(localPath.Length).Replace('\\', '/').TrimStart('/');
                progress("Uploading " + (i + 1) + " of " + files.Count + ": " + relative, 0.9f + 0.1f * i / files.Count);
                var response = client.Upload("/" + folder + "/" + relative, files[i]);
                total += response != null ? response.size : 0;
            }
            report.Notes.Add("Uploaded " + files.Count + " files, " + (total / (1024.0 * 1024.0)).ToString("0.0") + " MB, into My Files/" + folder);
        }
    }
}
