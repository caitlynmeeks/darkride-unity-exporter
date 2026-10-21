// Walks the scene (or a selection) and decides what becomes an entity: a MeshRenderer with a mesh is a Model, an
// enabled Mixed or Realtime point or spot light is a Light, a Mixed or Realtime directional light becomes the zone's
// key light, a Baked light is in the lightmaps and only listed, and a parent of exported things becomes an Empty
// (the editor's invisible, collisionless cube) so that a prefab instance moves as one. Positions and rotations are
// converted to darkride's frame here; a Model entity's position is the centre of its mesh bounds, as the engine
// places a model by its registration point (the middle of its box by default).

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Darkride.Exporter
{
    public class ExportEntity
    {
        public string Id = "{" + System.Guid.NewGuid().ToString() + "}";
        public string Type;                       // Model, Shape, Light, Zone
        public string Name;
        public Transform Source;
        public ExportEntity Parent;
        public Vector3 Position;                  // darkride world frame
        public Quaternion Rotation = Quaternion.identity;
        public Vector3 Dimensions = Vector3.one;
        public Dictionary<string, object> Properties = new Dictionary<string, object>();
        public Dictionary<string, object> UserData = new Dictionary<string, object>();

        // models
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public Material[] Materials;
        public int LightmapIndex = -1;
        public Vector4 LightmapScaleOffset;
        public Vector3 Mirror = Vector3.one;     // -1 on the axes Unity's scale negates: the mesh is written mirrored
    }

    public class Report
    {
        public List<string> Warnings = new List<string>();
        public List<string> BakedLights = new List<string>();
        public Dictionary<string, int> Counts = new Dictionary<string, int>();
        public List<string> Notes = new List<string>();

        public void Warn(string text)
        {
            Warnings.Add(text);
            Debug.LogWarning("[darkride] " + text);
        }

        public void Count(string key, int n = 1)
        {
            Counts.TryGetValue(key, out int current);
            Counts[key] = current + n;
        }

        public string Text()
        {
            var sb = new StringBuilder();
            foreach (string note in Notes) sb.AppendLine(note);
            if (Counts.Count > 0)
            {
                sb.AppendLine();
                foreach (var kv in Counts) sb.AppendLine(kv.Value + " " + kv.Key);
            }
            if (BakedLights.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Baked lights, in the lightmaps and not exported as lights:");
                foreach (string light in BakedLights) sb.AppendLine("  " + light);
            }
            if (Warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Warnings:");
                foreach (string warning in Warnings) sb.AppendLine("  " + warning);
            }
            return sb.ToString();
        }
    }

    public class SceneCollector
    {
        public List<ExportEntity> Entities = new List<ExportEntity>();
        public List<Light> DirectionalLights = new List<Light>();
        public HashSet<int> LightmapIndices = new HashSet<int>();
        public Bounds? WorldBounds;               // Unity frame, the union of the exported renderers' bounds

        readonly ExportOptions options;
        readonly Report report;
        readonly Dictionary<Transform, bool> exportableBelow = new Dictionary<Transform, bool>();
        readonly int lightmapCount;

        public SceneCollector(ExportOptions options, Report report)
        {
            this.options = options;
            this.report = report;
            var lightmaps = LightmapSettings.lightmaps;
            lightmapCount = lightmaps != null ? lightmaps.Length : 0;
        }

        public void Collect(IEnumerable<GameObject> roots)
        {
            foreach (var root in roots)
            {
                if (root != null) Walk(root.transform, null);
            }
        }

        void Walk(Transform t, ExportEntity parent)
        {
            var go = t.gameObject;
            if (!go.activeInHierarchy)
            {
                report.Count("inactive objects skipped (with their children)");
                return;
            }

            ExportEntity mine = null;
            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var filter = go.GetComponent<MeshFilter>();
                if (!renderer.enabled) report.Count("disabled renderers skipped");
                else if (filter == null || filter.sharedMesh == null) report.Warn(PathOf(t) + ": MeshRenderer without a mesh, skipped");
                else mine = MakeModel(t, renderer, filter.sharedMesh);
            }
            if (mine == null) mine = MakeCollisionOnly(t);
            if (go.GetComponent<SkinnedMeshRenderer>() != null) report.Warn(PathOf(t) + ": skinned mesh not exported (not supported yet)");
            if (go.GetComponent<Terrain>() != null) report.Warn(PathOf(t) + ": Unity Terrain not exported (darkride has its own terrain entity)");
            if (go.GetComponent<ParticleSystem>() != null) report.Count("particle systems skipped");
            if (go.GetComponent<Canvas>() != null) report.Count("UI canvases skipped");

            ExportEntity lightEntity = null;
            var light = go.GetComponent<Light>();
            if (light != null && light.enabled && options.ExportLights) lightEntity = MakeLight(t, light);
            if (mine == null && lightEntity != null)
            {
                mine = lightEntity;
                lightEntity = null;
            }
            if (mine == null && options.KeepHierarchy && HasExportableBelow(t)) mine = MakeEmpty(t);

            if (mine != null)
            {
                mine.Parent = options.KeepHierarchy ? parent : null;
                Entities.Add(mine);
            }
            if (lightEntity != null)
            {
                lightEntity.Parent = options.KeepHierarchy ? mine : null;
                Entities.Add(lightEntity);
            }

            var next = mine ?? parent;
            for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), next);
        }

        bool HasExportableBelow(Transform t)
        {
            if (exportableBelow.TryGetValue(t, out bool known)) return known;
            bool found = false;
            for (int i = 0; i < t.childCount && !found; i++)
            {
                var child = t.GetChild(i);
                if (!child.gameObject.activeInHierarchy) continue;
                found = IsExportable(child) || HasExportableBelow(child);
            }
            exportableBelow[t] = found;
            return found;
        }

        bool IsExportable(Transform t)
        {
            var renderer = t.GetComponent<MeshRenderer>();
            var filter = t.GetComponent<MeshFilter>();
            if (renderer != null && renderer.enabled && filter != null && filter.sharedMesh != null) return true;
            var light = t.GetComponent<Light>();
            if (light != null && light.enabled && options.ExportLights && light.lightmapBakeType != LightmapBakeType.Baked &&
                (light.type == LightType.Point || light.type == LightType.Spot)) return true;
            return SolidCollider(t) != null;
        }

        // The first enabled, non-trigger collider on the object
        static Collider SolidCollider(Transform t)
        {
            foreach (var collider in t.GetComponents<Collider>())
                if (collider.enabled && !collider.isTrigger) return collider;
            return null;
        }

        // An object with a collider and nothing drawn (a disabled renderer, or none): Unity's way of giving a wall
        // or a floor its collision. It becomes an invisible shape the avatar collides with.
        ExportEntity MakeCollisionOnly(Transform t)
        {
            var collider = SolidCollider(t);
            if (collider == null) return null;
            var e = new ExportEntity { Name = t.name, Source = t };
            Vector3 scale = Conversions.Size(t.lossyScale);
            e.Rotation = Conversions.Rotation(t.rotation);
            if (collider is BoxCollider box)
            {
                e.Type = "Shape";
                e.Properties["shape"] = "Cube";
                e.Properties["shapeType"] = "box";
                e.Position = Conversions.Point(t.TransformPoint(box.center));
                e.Dimensions = Vector3.Max(Vector3.Scale(box.size, scale), Vector3.one * 0.001f);
            }
            else if (collider is SphereCollider sphere)
            {
                e.Type = "Shape";
                e.Properties["shape"] = "Sphere";
                e.Properties["shapeType"] = "sphere";
                e.Position = Conversions.Point(t.TransformPoint(sphere.center));
                float d = sphere.radius * 2 * Mathf.Max(scale.x, scale.y, scale.z);
                e.Dimensions = Vector3.one * Mathf.Max(d, 0.001f);
            }
            else if (collider is CapsuleCollider capsule)
            {
                e.Type = "Shape";
                e.Properties["shape"] = "Cylinder";
                e.Properties["shapeType"] = "capsule-y";
                e.Position = Conversions.Point(t.TransformPoint(capsule.center));
                float d = capsule.radius * 2 * Mathf.Max(scale.x, scale.z);
                e.Dimensions = new Vector3(d, Mathf.Max(capsule.height * scale.y, d), d);
            }
            else if (collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
            {
                e.Type = "Model";
                e.Mesh = meshCollider.sharedMesh;
                e.Materials = new Material[0];
                e.Properties["shapeType"] = meshCollider.convex ? "simple-hull" : "static-mesh";
                Bounds local = VertexBounds(e.Mesh);
                e.Position = Conversions.Point(t.TransformPoint(local.center));
                e.Dimensions = Vector3.Max(Vector3.Scale(scale, local.size), Vector3.one * 0.001f);
                if (t.lossyScale.x < 0 || t.lossyScale.y < 0 || t.lossyScale.z < 0)
                    e.Mirror = new Vector3(t.lossyScale.x < 0 ? -1 : 1, t.lossyScale.y < 0 ? -1 : 1, t.lossyScale.z < 0 ? -1 : 1);
            }
            else
            {
                return null;
            }
            e.Properties["visible"] = false;
            e.Properties["canCastShadow"] = false;
            report.Count("collision-only objects (invisible shapes)");
            return e;
        }

        ExportEntity MakeModel(Transform t, MeshRenderer renderer, Mesh mesh)
        {
            var e = new ExportEntity
            {
                Type = "Model",
                Name = t.name,
                Source = t,
                Renderer = renderer,
                Mesh = mesh,
                Materials = renderer.sharedMaterials,
            };
            Bounds local = VertexBounds(mesh);
            Vector3 scale = t.lossyScale;
            if (scale.x < 0 || scale.y < 0 || scale.z < 0)
            {
                // an entity's dimensions cannot be negative, so the mirror goes into a mesh file of its own
                e.Mirror = new Vector3(scale.x < 0 ? -1 : 1, scale.y < 0 ? -1 : 1, scale.z < 0 ? -1 : 1);
                report.Count("mirrored instances (negative scale: a mirrored mesh file)");
            }
            e.Position = Conversions.Point(t.TransformPoint(local.center));
            e.Rotation = Conversions.Rotation(t.rotation);
            e.Dimensions = Vector3.Max(Vector3.Scale(Conversions.Size(scale), local.size), Vector3.one * 0.001f);

            int index = renderer.lightmapIndex;
            if (index >= 0 && index < 65534)
            {
                if (index < lightmapCount)
                {
                    e.LightmapIndex = index;
                    e.LightmapScaleOffset = renderer.lightmapScaleOffset;
                    LightmapIndices.Add(index);
                    report.Count("lightmapped renderers");
                }
                else
                {
                    report.Warn(PathOf(t) + ": lightmap index " + index + " beyond the scene's " + lightmapCount + " lightmaps; the scene needs a bake");
                }
            }
            else
            {
                report.Count("renderers without a lightmap (lit by the zone's ambient)");
            }

            e.Properties["shapeType"] = ShapeTypeOf(t, out bool collisionless);
            if (collisionless) e.Properties["collisionless"] = true;
            if (!renderer.shadowCastingMode.Equals(UnityEngine.Rendering.ShadowCastingMode.Off)) e.Properties["canCastShadow"] = true;
            else e.Properties["canCastShadow"] = false;

            Bounds world = renderer.bounds;
            if (WorldBounds.HasValue)
            {
                var b = WorldBounds.Value;
                b.Encapsulate(world);
                WorldBounds = b;
            }
            else
            {
                WorldBounds = world;
            }
            report.Count("models");
            return e;
        }

        static Bounds VertexBounds(Mesh mesh)
        {
            try
            {
                var vertices = mesh.vertices;
                if (vertices != null && vertices.Length > 0)
                {
                    var b = new Bounds(vertices[0], Vector3.zero);
                    for (int i = 1; i < vertices.Length; i++) b.Encapsulate(vertices[i]);
                    return b;
                }
            }
            catch (System.Exception)
            {
                // a mesh whose vertices cannot be read: its bounds will do
            }
            return mesh.bounds;
        }

        static string ShapeTypeOf(Transform t, out bool collisionless)
        {
            collisionless = false;
            var collider = SolidCollider(t);
            if (collider is MeshCollider meshCollider) return meshCollider.convex ? "simple-hull" : "static-mesh";
            if (collider is BoxCollider) return "box";
            if (collider is SphereCollider) return "sphere";
            if (collider is CapsuleCollider) return "capsule-y";
            collisionless = true;
            return "none";
        }

        ExportEntity MakeLight(Transform t, Light light)
        {
            if (light.lightmapBakeType == LightmapBakeType.Baked)
            {
                report.BakedLights.Add(PathOf(t) + " (" + light.type + ")");
                return null;
            }
            if (light.type == LightType.Directional)
            {
                DirectionalLights.Add(light);
                return null;
            }
            if (light.type != LightType.Point && light.type != LightType.Spot)
            {
                report.Warn(PathOf(t) + ": " + light.type + " light not exported (only point and spot lights have an entity type)");
                return null;
            }
            var e = new ExportEntity { Type = "Light", Name = t.name, Source = t };
            e.Position = Conversions.Point(t.position);
            var rotation = Conversions.Rotation(t.rotation);
            e.Rotation = light.type == LightType.Spot ? rotation * Conversions.SpotFlip : rotation;
            float range = Mathf.Max(light.range, 0.01f);
            e.Dimensions = Vector3.one * (range * 2);
            e.Properties["color"] = (Color32)light.color;
            e.Properties["intensity"] = light.intensity;
            e.Properties["falloffRadius"] = 1.0f;
            e.Properties["isSpotlight"] = light.type == LightType.Spot;
            if (light.type == LightType.Spot)
            {
                e.Properties["cutoff"] = light.spotAngle / 2;
                e.Properties["exponent"] = 1.0f;
            }
            e.Properties["canCastShadow"] = light.shadows != LightShadows.None;
            // a light casts in the engine only with its userData entry "shadows" (the Inspector's Shadows field On);
            // Cast Shadows alone is on for every entity, so it no longer turns a light's shadows on (2026-10-21)
            if (light.shadows != LightShadows.None) e.UserData["shadows"] = "on";
            report.Count(light.lightmapBakeType + " " + light.type.ToString().ToLowerInvariant() + " lights exported (intensity as in Unity, not yet calibrated)");
            return e;
        }

        ExportEntity MakeEmpty(Transform t)
        {
            var e = new ExportEntity { Type = "Shape", Name = t.name, Source = t };
            e.Position = Conversions.Point(t.position);
            e.Rotation = Conversions.Rotation(t.rotation);
            e.Dimensions = Vector3.one * 0.2f;
            e.Properties["shape"] = "Cube";
            e.Properties["visible"] = false;
            e.Properties["collisionless"] = true;
            report.Count("empties (parents)");
            return e;
        }

        public static string PathOf(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
