// The probe volume (LIGHTMAPS.md step 4): Unity's Adaptive Probe Volume bake, resampled onto a uniform grid over the
// scene's box and written as the engine's probe volume file, probes/ProbeVolume.bin, which the zone's userData
// "probeVolume" names. The engine samples it by world position for the ambient diffuse light of whatever has no
// lightmap (avatars, things that move), in place of the zone's one ambient sphere.
//
// Unity's data: the scene's baking set (a ProbeVolumeBakingSet asset, read through SerializedObject since its fields
// are internal) lists its cells, and its streamable assets (.bytes TextAssets beside it) hold, per cell and chunk
// of 128 bricks: the bricks (CellBricksData: position in units of the smallest brick, subdivision level), the
// probes' L0 and L1 (CellData: L0 rgb and L1R.x as halves, then L1G.xyz + L1R.y and L1B.xyz + L1R.z as bytes), L2
// (CellOptionalData: four byte textures), and the support data (CellSupportData: positions, validity, touch-up,
// offsets). A brick is 4 x 4 x 4 probes, its probes 3^level x the minimum distance apart, laid in the chunk as the
// GPU pool has them (brick b at x = 4 b). The SH is decoded as Unity's shader does (DecodeSH.hlsl): L1 = (enc - 0.5)
// x 4 L0, L2 = (enc - 0.5) x 3.5777 L0 with the zz term taken out of L0 and tripled, which gives Unity's polynomial
//   p(n) = A + Bx x + By y + Bz z + Cxy xy + Cyz yz + Czz z^2 + Cxz xz + Cc (x^2 - y^2)
// (SHEvalLinearL1 + SHEvalLinearL2), the baked diffuse light for a normal n, what a lightmap texel holds for its
// surface. A probe whose validity (the share of its rays that hit back faces) is above Unity's threshold is left out
// of the blend, as Unity's runtime leaves it out.
//
// The grid: the scene's box (the zone's), snapped outward to the bake's probe lattice at the chosen spacing, so a
// grid probe falls on a baked one where the bake is finest; each grid probe is the trilinear blend of the valid
// probes of the finest brick around it, a grid probe with no valid one is filled from its neighbours afterwards
// (dilation). The polynomial becomes the engine's spherical harmonics (SphericalHarmonics.shared.slh, where
// E(n) = C4 L00 - C5 L20 + C3 L20 z^2 + C1 L22 (x^2 - y^2) + 2 C1 (L2m2 xy + L21 xz + L2m1 yz) + 2 C2 (L11 x +
// L1m1 y + L10 z)): L00 = (A + Czz / 3) / C4, L1m1 = By / 2 C2, L10 = Bz / 2 C2, L11 = Bx / 2 C2, L2m2 = Cxy / 2 C1,
// L2m1 = Cyz / 2 C1, L20 = Czz / C3, L21 = Cxz / 2 C1, L22 = Cc / C1; then the frame's X mirror reverses the grid
// along x and changes the sign of the coefficients odd in x (L11, L2m2, L21).
//
// The file: "DRPV", u32 version 1, u32 probes per axis W H D, f32 origin x y z (the engine-frame world position of
// probe 0, 0, 0), f32 spacing x y z, u32 coefficients 9, u32 format 1 (RGBA halves); then the engine's 3D texture's
// texels, W by 9 H by D, x fastest, then the coefficient block and the probe row, then the slice: L00, L1m1, L10,
// L11, L2m2, L2m1, L20, L21, L22, RGB, the alpha of the first 1 for a probe blended from baked ones and 0 for a
// filled one.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Darkride.Exporter
{
    public static class ProbeVolumeExport
    {
        const float VALIDITY_THRESHOLD = 0.25f;    // Unity's dilation validity threshold
        const float L1_SCALE = 2.0f;                // DecodeSH.hlsl APV_L1_ENCODING_SCALE
        const float L2_SCALE = 3.5777088f;          // APV_L2_ENCODING_SCALE
        const float C1 = 0.429043f, C2 = 0.511664f, C3 = 0.743125f, C4 = 0.886227f, C5 = 0.247708f;
        const int MAX_TEXTURE_DIMENSION = 2048;     // the least a GL implementation allows a 3D texture

        class Brick
        {
            public Vector3 Corner;
            public float Size;
            public int Level;
            public int FirstProbe;          // index of its probe (0, 0, 0) in the arrays below; x fastest, then y, then z
        }

        class Bake
        {
            public List<Brick> Bricks = new List<Brick>();
            public List<Vector3> Positions = new List<Vector3>();
            public List<float> Validity = new List<float>();
            public List<float[]> Poly = new List<float[]>();   // 27 a probe: the 9 polynomial coefficients, RGB each
            public float MinDistance;
            public int Cells;
        }

        public class Written
        {
            public string File;
            public Vector3Int Probes;
            public float Spacing;
        }

        /// <summary>Resamples the scene's APV bake over box (Unity frame) at spacing and writes probesDir/fileName;
        /// null, with a note or a warning, when the scene has no bake or the grid would be too big.</summary>
        public static Written Write(Bounds box, float spacing, string probesDir, string fileName, Report report)
        {
            if (spacing <= 0.01f)
            {
                report.Warn("Probe volume spacing " + spacing + " m is too small; no probe volume written");
                return null;
            }
            Bake bake;
            try
            {
                bake = Read(report);
            }
            catch (Exception e)
            {
                report.Warn("The probe volume bake could not be read: " + e.Message);
                return null;
            }
            if (bake == null) return null;

            // the grid, snapped outward to the bake's lattice (whose probes sit at multiples of the minimum distance
            // from the bake's offset, here taken as 0): where the spacing is a multiple of that distance a grid probe
            // falls on a baked one
            Vector3 min = new Vector3(Mathf.Floor(box.min.x / spacing), Mathf.Floor(box.min.y / spacing), Mathf.Floor(box.min.z / spacing)) * spacing;
            Vector3 max = new Vector3(Mathf.Ceil(box.max.x / spacing), Mathf.Ceil(box.max.y / spacing), Mathf.Ceil(box.max.z / spacing)) * spacing;
            int w = Mathf.RoundToInt((max.x - min.x) / spacing) + 1;
            int h = Mathf.RoundToInt((max.y - min.y) / spacing) + 1;
            int d = Mathf.RoundToInt((max.z - min.z) / spacing) + 1;
            if (w > MAX_TEXTURE_DIMENSION || 9 * h > MAX_TEXTURE_DIMENSION || d > MAX_TEXTURE_DIMENSION)
            {
                report.Warn("The probe volume grid would be " + w + " x " + h + " x " + d + " probes at " + spacing + " m (at most " + MAX_TEXTURE_DIMENSION + " x " + (MAX_TEXTURE_DIMENSION / 9) + " x " + MAX_TEXTURE_DIMENSION + "); a wider spacing is needed; no probe volume written");
                return null;
            }

            // the bricks by level, finest first, each level's by lattice cell for the lookup
            var byLevel = new List<List<Brick>>();
            var lookup = new List<Dictionary<Vector3Int, Brick>>();
            int maxLevel = 0;
            foreach (var b in bake.Bricks) maxLevel = Math.Max(maxLevel, b.Level);
            for (int l = 0; l <= maxLevel; l++) { byLevel.Add(new List<Brick>()); lookup.Add(new Dictionary<Vector3Int, Brick>()); }
            foreach (var b in bake.Bricks)
            {
                byLevel[b.Level].Add(b);
                lookup[b.Level][Key(b.Corner, b.Size)] = b;
            }

            var grid = new float[w * h * d][];
            var valid = new bool[w * h * d];
            int validCount = 0;
            for (int z = 0; z < d; z++)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var p = min + new Vector3(x, y, z) * spacing;
                        var s = Sample(bake, byLevel, lookup, p);
                        int i = x + w * (y + h * z);
                        if (s != null) { grid[i] = s; valid[i] = true; validCount++; }
                    }

            // dilation: a grid probe with no valid baked probe around it takes the mean of its filled neighbours, pass
            // after pass; whatever is left (a grid of nothing valid) takes the mean of all
            var filled = (bool[])valid.Clone();
            int passes = 0;
            for (; passes < 64; passes++)
            {
                var additions = new List<(int, float[])>();
                for (int z = 0; z < d; z++)
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int i = x + w * (y + h * z);
                            if (filled[i]) continue;
                            var acc = new float[27]; int n = 0;
                            foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1) })
                            {
                                int xx = x + dx, yy = y + dy, zz = z + dz;
                                if (xx < 0 || xx >= w || yy < 0 || yy >= h || zz < 0 || zz >= d) continue;
                                int j = xx + w * (yy + h * zz);
                                if (!filled[j]) continue;
                                for (int k = 0; k < 27; k++) acc[k] += grid[j][k];
                                n++;
                            }
                            if (n > 0)
                            {
                                for (int k = 0; k < 27; k++) acc[k] /= n;
                                additions.Add((i, acc));
                            }
                        }
                if (additions.Count == 0) break;
                foreach (var (i, v) in additions) { grid[i] = v; filled[i] = true; }
            }
            int unfilled = 0;
            {
                var mean = new float[27]; int n = 0;
                for (int i = 0; i < grid.Length; i++) if (filled[i]) { for (int k = 0; k < 27; k++) mean[k] += grid[i][k]; n++; }
                if (n > 0) for (int k = 0; k < 27; k++) mean[k] /= n;
                for (int i = 0; i < grid.Length; i++) if (!filled[i]) { grid[i] = mean; unfilled++; }
            }

            // the engine's frame: x mirrored, so the grid's x runs the other way and its origin is the mirrored far end
            var origin = new Vector3(-(min.x + (w - 1) * spacing), min.y, min.z);
            string path = Path.Combine(probesDir, fileName);
            using (var f = new BinaryWriter(File.Open(path, FileMode.Create)))
            {
                f.Write(new[] { (byte)'D', (byte)'R', (byte)'P', (byte)'V' });
                f.Write((uint)1); f.Write((uint)w); f.Write((uint)h); f.Write((uint)d);
                f.Write(origin.x); f.Write(origin.y); f.Write(origin.z);
                f.Write(spacing); f.Write(spacing); f.Write(spacing);
                f.Write((uint)9); f.Write((uint)1);
                var sh = new float[9];
                for (int z = 0; z < d; z++)
                    for (int c = 0; c < 9; c++)
                        for (int y = 0; y < h; y++)
                            for (int xe = 0; xe < w; xe++)
                            {
                                int i = (w - 1 - xe) + w * (y + h * z);
                                var poly = grid[i];
                                for (int ch = 0; ch < 3; ch++)
                                {
                                    float A = poly[0 * 3 + ch], Bx = poly[1 * 3 + ch], By = poly[2 * 3 + ch], Bz = poly[3 * 3 + ch];
                                    float Cxy = poly[4 * 3 + ch], Cyz = poly[5 * 3 + ch], Czz = poly[6 * 3 + ch], Cxz = poly[7 * 3 + ch], Cc = poly[8 * 3 + ch];
                                    sh[0] = (A + Czz / 3.0f) / C4;
                                    sh[1] = By / (2 * C2);
                                    sh[2] = Bz / (2 * C2);
                                    sh[3] = -Bx / (2 * C2);
                                    sh[4] = -Cxy / (2 * C1);
                                    sh[5] = Cyz / (2 * C1);
                                    sh[6] = Czz / C3;
                                    sh[7] = -Cxz / (2 * C1);
                                    sh[8] = Cc / C1;
                                    f.Write(Mathf.FloatToHalf(sh[c]));
                                }
                                f.Write(Mathf.FloatToHalf(c == 0 && valid[i] ? 1.0f : 0.0f));
                            }
            }
            report.Notes.Add("Probe volume: " + bake.Bricks.Count + " bricks and " + bake.Positions.Count + " probes in " + bake.Cells + " cells, " + bake.MinDistance + " m apart at the finest, resampled as " + w + " x " + h + " x " + d + " probes " + spacing + " m apart (" + validCount + " from valid probes, " + (grid.Length - validCount - unfilled) + " filled from neighbours in " + passes + " passes" + (unfilled > 0 ? ", " + unfilled + " from the mean" : "") + ") into " + fileName + ", " + (new FileInfo(path).Length / 1024) + " KB");
            return new Written { File = fileName, Probes = new Vector3Int(w, h, d), Spacing = spacing };
        }

        static Vector3Int Key(Vector3 p, float size)
        {
            return new Vector3Int(Mathf.FloorToInt(p.x / size + 1e-4f), Mathf.FloorToInt(p.y / size + 1e-4f), Mathf.FloorToInt(p.z / size + 1e-4f));
        }

        static bool Contains(Brick b, Vector3 p)
        {
            const float e = 1e-4f;
            return p.x >= b.Corner.x - e && p.x <= b.Corner.x + b.Size + e
                && p.y >= b.Corner.y - e && p.y <= b.Corner.y + b.Size + e
                && p.z >= b.Corner.z - e && p.z <= b.Corner.z + b.Size + e;
        }

        // the trilinear blend of the valid probes of the finest brick around p; null with none
        static float[] Sample(Bake bake, List<List<Brick>> byLevel, List<Dictionary<Vector3Int, Brick>> lookup, Vector3 p)
        {
            for (int level = 0; level < byLevel.Count; level++)
            {
                if (byLevel[level].Count == 0) continue;
                float size = byLevel[level][0].Size;
                Brick brick = null;
                // the lattice cell p is in, then, at a cell's far face, the cell before it; then the slow way
                var key = Key(p, size);
                if (!lookup[level].TryGetValue(key, out brick) || !Contains(brick, p))
                {
                    brick = null;
                    foreach (var dx in new[] { 0, -1 })
                        foreach (var dy in new[] { 0, -1 })
                            foreach (var dz in new[] { 0, -1 })
                            {
                                if (lookup[level].TryGetValue(key + new Vector3Int(dx, dy, dz), out var candidate) && Contains(candidate, p)) { brick = candidate; break; }
                            }
                    if (brick == null)
                        foreach (var candidate in byLevel[level]) if (Contains(candidate, p)) { brick = candidate; break; }
                }
                if (brick == null) continue;

                var t = (p - brick.Corner) / (brick.Size / 3.0f);
                var i0 = new Vector3Int(Mathf.Min(Mathf.FloorToInt(t.x), 2), Mathf.Min(Mathf.FloorToInt(t.y), 2), Mathf.Min(Mathf.FloorToInt(t.z), 2));
                i0 = Vector3Int.Max(i0, Vector3Int.zero);
                var f = new Vector3(Mathf.Clamp01(t.x - i0.x), Mathf.Clamp01(t.y - i0.y), Mathf.Clamp01(t.z - i0.z));
                var acc = new float[27]; float wsum = 0;
                for (int dz = 0; dz < 2; dz++)
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            float wgt = (dx == 1 ? f.x : 1 - f.x) * (dy == 1 ? f.y : 1 - f.y) * (dz == 1 ? f.z : 1 - f.z);
                            if (wgt <= 0) continue;
                            int idx = brick.FirstProbe + (i0.x + dx) + 4 * ((i0.y + dy) + 4 * (i0.z + dz));
                            if (bake.Validity[idx] > VALIDITY_THRESHOLD) continue;
                            var poly = bake.Poly[idx];
                            for (int k = 0; k < 27; k++) acc[k] += wgt * poly[k];
                            wsum += wgt;
                        }
                if (wsum > 0)
                {
                    for (int k = 0; k < 27; k++) acc[k] /= wsum;
                    return acc;
                }
                // every probe of the finest brick here is inside geometry (a grid probe in a floor or a wall): left
                // for the dilation, which fills it from its grid neighbours in the open, as Unity's runtime reweights
                // toward the valid probes beside such a spot; a coarser brick's probes would give the dark of the inside
                return null;
            }
            return null;
        }

        // Unity's bake, decoded: null with a note when the scene has none
        static Bake Read(Report report)
        {
            var scene = SceneManager.GetActiveScene();
            string sceneGuid = AssetDatabase.AssetPathToGUID(scene.path);
            ScriptableObject setAsset = null;
            foreach (string guid in AssetDatabase.FindAssets("t:ProbeVolumeBakingSet"))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<ScriptableObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate == null) continue;
                var scenes = new SerializedObject(candidate).FindProperty("m_SceneGUIDs");
                for (int i = 0; scenes != null && i < scenes.arraySize; i++)
                    if (scenes.GetArrayElementAtIndex(i).stringValue == sceneGuid) { setAsset = candidate; break; }
                if (setAsset != null) break;
            }
            if (setAsset == null)
            {
                report.Notes.Add("No probe volume: the scene is in no Adaptive Probe Volume baking set (Lighting > Adaptive Probe Volumes)");
                return null;
            }
            var so = new SerializedObject(setAsset);
            int chunkBricks = so.FindProperty("chunkSizeInBricks").intValue;
            int chunkProbes = chunkBricks * 64;
            int L0ChunkSize = so.FindProperty("L0ChunkSize").intValue;
            int L1ChunkSize = so.FindProperty("L1ChunkSize").intValue;
            int L2ChunkSize = so.FindProperty("L2TextureChunkSize").intValue;
            int supPos = so.FindProperty("supportPositionChunkSize").intValue;
            int supVal = so.FindProperty("supportValidityChunkSize").intValue;
            float minDistance = so.FindProperty("bakedMinDistanceBetweenProbes").floatValue;
            Vector3 probeOffset = so.FindProperty("bakedProbeOffset").vector3Value;
            float minBrick = 3.0f * minDistance;
            if (chunkBricks <= 0 || L0ChunkSize != chunkProbes * 8 || L1ChunkSize != chunkProbes * 4 || supPos != chunkProbes * 12 || supVal != chunkProbes * 4)
            {
                report.Warn("The probe volume baking set " + setAsset.name + " has a layout this exporter does not know (chunk " + chunkBricks + " bricks, L0 " + L0ChunkSize + ", L1 " + L1ChunkSize + ", positions " + supPos + "); no probe volume written");
                return null;
            }
            bool hasL2 = L2ChunkSize == chunkProbes * 4;

            var bricksStream = ReadStream(so.FindProperty("cellBricksDataAsset"), "bricks", report);
            var supportStream = ReadStream(so.FindProperty("cellSupportDataAsset"), "support data", report);
            // the scenario: the lighting scenario named, else the first
            var scenarios = so.FindProperty("scenarios");
            var scenarioKeys = scenarios.FindPropertyRelative("m_Keys");
            var scenarioValues = scenarios.FindPropertyRelative("m_Values");
            string wanted = so.FindProperty("lightingScenario").stringValue;
            int scenarioIndex = 0;
            for (int i = 0; i < scenarioKeys.arraySize; i++) if (scenarioKeys.GetArrayElementAtIndex(i).stringValue == wanted) scenarioIndex = i;
            if (scenarioValues.arraySize == 0)
            {
                report.Notes.Add("No probe volume: the baking set " + setAsset.name + " has no baked lighting scenario (bake the scene)");
                return null;
            }
            var scenario = scenarioValues.GetArrayElementAtIndex(scenarioIndex);
            var dataStream = ReadStream(scenario.FindPropertyRelative("cellDataAsset"), "L0 and L1 data", report);
            var optionalStream = hasL2 ? ReadStream(scenario.FindPropertyRelative("cellOptionalDataAsset"), "L2 data", report) : null;
            if (bricksStream == null || supportStream == null || dataStream == null)
            {
                report.Warn("The probe volume baking set " + setAsset.name + " is missing its data; bake the scene; no probe volume written");
                return null;
            }
            if (hasL2 && optionalStream == null)
            {
                report.Notes.Add("Probe volume: no L2 data; L1 only");
                hasL2 = false;
            }

            var bake = new Bake { MinDistance = minDistance };
            var cellKeys = so.FindProperty("cellDescs").FindPropertyRelative("m_Keys");
            var cellValues = so.FindProperty("cellDescs").FindPropertyRelative("m_Values");
            for (int ci = 0; ci < cellKeys.arraySize; ci++)
            {
                int key = cellKeys.GetArrayElementAtIndex(ci).intValue;
                var cell = cellValues.GetArrayElementAtIndex(ci);
                int chunks = cell.FindPropertyRelative("shChunkCount").intValue;
                int bricksCount = cell.FindPropertyRelative("bricksCount").intValue;
                if (!bricksStream.Cells.TryGetValue(key, out var bricksDesc) || !dataStream.Cells.TryGetValue(key, out var dataDesc) || !supportStream.Cells.TryGetValue(key, out var supportDesc))
                {
                    report.Warn("Probe volume cell " + key + " has no data; skipped");
                    continue;
                }
                (int offset, int count) optionalDesc = (0, 0);
                if (hasL2) optionalStream.Cells.TryGetValue(key, out optionalDesc);
                bake.Cells++;
                var bytes = dataStream.Bytes; var sbytes = supportStream.Bytes;
                int cellL0 = dataDesc.offset;
                int cellL1G = cellL0 + chunks * L0ChunkSize;
                int cellL1B = cellL1G + chunks * L1ChunkSize;
                int cellPos = supportDesc.offset;
                int cellVal = cellPos + chunks * supPos;
                for (int b = 0; b < bricksCount; b++)
                {
                    int bo = bricksDesc.offset + 16 * b;
                    var bpos = new Vector3Int(BitConverter.ToInt32(bricksStream.Bytes, bo), BitConverter.ToInt32(bricksStream.Bytes, bo + 4), BitConverter.ToInt32(bricksStream.Bytes, bo + 8));
                    int level = BitConverter.ToInt32(bricksStream.Bytes, bo + 12);
                    var brick = new Brick { Corner = probeOffset + (Vector3)bpos * minBrick, Size = minBrick * Mathf.Pow(3, level), Level = level, FirstProbe = bake.Positions.Count };
                    bake.Bricks.Add(brick);
                    int chunk = b / chunkBricks, bc = b % chunkBricks;
                    for (int z = 0; z < 4; z++)
                        for (int y = 0; y < 4; y++)
                            for (int x = 0; x < 4; x++)
                            {
                                int i = (bc * 4 + x) + chunkBricks * 4 * (y + 4 * z);   // the chunk's data location: bricks side by side in x
                                int l0 = cellL0 + chunk * L0ChunkSize + i * 8;
                                int l1g = cellL1G + chunk * L1ChunkSize + i * 4;
                                int l1b = cellL1B + chunk * L1ChunkSize + i * 4;
                                var L0 = new Vector3(Half(bytes, l0), Half(bytes, l0 + 2), Half(bytes, l0 + 4));
                                var L1R = new Vector3(Half(bytes, l0 + 6), bytes[l1g + 3] / 255f, bytes[l1b + 3] / 255f);
                                var L1G = new Vector3(bytes[l1g] / 255f, bytes[l1g + 1] / 255f, bytes[l1g + 2] / 255f);
                                var L1B = new Vector3(bytes[l1b] / 255f, bytes[l1b + 1] / 255f, bytes[l1b + 2] / 255f);
                                L1R = (L1R - 0.5f * Vector3.one) * (2 * L1_SCALE * L0.x);
                                L1G = (L1G - 0.5f * Vector3.one) * (2 * L1_SCALE * L0.y);
                                L1B = (L1B - 0.5f * Vector3.one) * (2 * L1_SCALE * L0.z);
                                var poly = new float[27];
                                Vector3 A = L0;
                                var zz = Vector3.zero; var cxy = Vector3.zero; var cyz = Vector3.zero; var cxz = Vector3.zero; var cc = Vector3.zero;
                                if (hasL2)
                                {
                                    var obytes = optionalStream.Bytes;
                                    int o0 = optionalDesc.offset + chunks * L2ChunkSize * 0 + chunk * L2ChunkSize + i * 4;
                                    int o1 = optionalDesc.offset + chunks * L2ChunkSize * 1 + chunk * L2ChunkSize + i * 4;
                                    int o2 = optionalDesc.offset + chunks * L2ChunkSize * 2 + chunk * L2ChunkSize + i * 4;
                                    int o3 = optionalDesc.offset + chunks * L2ChunkSize * 3 + chunk * L2ChunkSize + i * 4;
                                    var L2R = Decode4(obytes, o0, L2_SCALE * L0.x);
                                    var L2G = Decode4(obytes, o1, L2_SCALE * L0.y);
                                    var L2B = Decode4(obytes, o2, L2_SCALE * L0.z);
                                    var L2C = new Vector3((obytes[o3] / 255f - 0.5f) * L2_SCALE * L0.x, (obytes[o3 + 1] / 255f - 0.5f) * L2_SCALE * L0.y, (obytes[o3 + 2] / 255f - 0.5f) * L2_SCALE * L0.z);
                                    A = L0 - new Vector3(L2R.z, L2G.z, L2B.z);
                                    zz = 3.0f * new Vector3(L2R.z, L2G.z, L2B.z);
                                    cxy = new Vector3(L2R.x, L2G.x, L2B.x);
                                    cyz = new Vector3(L2R.y, L2G.y, L2B.y);
                                    cxz = new Vector3(L2R.w, L2G.w, L2B.w);
                                    cc = L2C;
                                }
                                Put(poly, 0, A);
                                Put(poly, 1, new Vector3(L1R.x, L1G.x, L1B.x));
                                Put(poly, 2, new Vector3(L1R.y, L1G.y, L1B.y));
                                Put(poly, 3, new Vector3(L1R.z, L1G.z, L1B.z));
                                Put(poly, 4, cxy); Put(poly, 5, cyz); Put(poly, 6, zz); Put(poly, 7, cxz); Put(poly, 8, cc);
                                bake.Poly.Add(poly);
                                int po = cellPos + chunk * supPos + i * 12;
                                bake.Positions.Add(new Vector3(BitConverter.ToSingle(sbytes, po), BitConverter.ToSingle(sbytes, po + 4), BitConverter.ToSingle(sbytes, po + 8)));
                                bake.Validity.Add(BitConverter.ToSingle(sbytes, cellVal + chunk * supVal + i * 4));
                            }
                }
            }
            if (bake.Bricks.Count == 0)
            {
                report.Notes.Add("No probe volume: the baking set " + setAsset.name + " has no bricks (bake the scene)");
                return null;
            }
            // a check of the layout: the first probe of a brick sits at its corner
            int mismatches = 0;
            foreach (var b in bake.Bricks)
                if ((bake.Positions[b.FirstProbe] - b.Corner).sqrMagnitude > 1e-4f) mismatches++;
            if (mismatches > 0) report.Warn("Probe volume: " + mismatches + " of " + bake.Bricks.Count + " bricks have their first probe away from their corner (an unexpected data layout); the volume may be wrong");
            return bake;
        }

        class Stream
        {
            public byte[] Bytes;
            public Dictionary<int, (int offset, int count)> Cells = new Dictionary<int, (int, int)>();
        }

        static Stream ReadStream(SerializedProperty asset, string what, Report report)
        {
            if (asset == null) return null;
            var text = asset.FindPropertyRelative("m_Asset").objectReferenceValue as TextAsset;
            if (text == null) return null;
            var stream = new Stream { Bytes = text.bytes };
            var descs = asset.FindPropertyRelative("m_StreamableCellDescs");
            var keys = descs.FindPropertyRelative("m_Keys");
            var values = descs.FindPropertyRelative("m_Values");
            for (int i = 0; i < keys.arraySize && i < values.arraySize; i++)
            {
                var v = values.GetArrayElementAtIndex(i);
                stream.Cells[keys.GetArrayElementAtIndex(i).intValue] = (v.FindPropertyRelative("offset").intValue, v.FindPropertyRelative("elementCount").intValue);
            }
            return stream;
        }

        static float Half(byte[] bytes, int offset) => Mathf.HalfToFloat(BitConverter.ToUInt16(bytes, offset));

        static Vector4 Decode4(byte[] bytes, int offset, float scale)
        {
            return new Vector4((bytes[offset] / 255f - 0.5f) * scale, (bytes[offset + 1] / 255f - 0.5f) * scale, (bytes[offset + 2] / 255f - 0.5f) * scale, (bytes[offset + 3] / 255f - 0.5f) * scale);
        }

        static void Put(float[] poly, int k, Vector3 v) { poly[k * 3] = v.x; poly[k * 3 + 1] = v.y; poly[k * 3 + 2] = v.z; }
    }
}
