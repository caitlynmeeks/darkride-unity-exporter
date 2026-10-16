// Unity's left-handed, Y-up frame to darkride's right-handed, Y-up frame: X is mirrored, as UnityGLTF does for the
// meshes, so a point, a direction and a rotation convert as below, and the mesh and the entity agree.

using UnityEngine;

namespace Darkride.Exporter
{
    public static class Conversions
    {
        public static Vector3 Point(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        public static Vector3 Direction(Vector3 d) => new Vector3(-d.x, d.y, d.z);

        // M R M for the mirror M = diag(-1, 1, 1): the axis mirrors and the sense of rotation flips
        public static Quaternion Rotation(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        public static Vector3 Size(Vector3 s) => new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));

        // A spot light in darkride shines along its entity's -Z; Unity's along its +Z
        public static readonly Quaternion SpotFlip = new Quaternion(0, 1, 0, 0);

        // Unity's lightmap scale and offset (uv2 * xy + zw, v up from the bottom) for glTF's second UV set, whose v
        // the exporter flips: (sx, sy, ox, 1 - sy - oy)
        public static Vector4 LightmapScaleOffset(Vector4 so) => new Vector4(so.x, so.y, so.z, 1 - so.y - so.w);

        // A linear colour as the engine's {red, green, blue} bytes (gamma encoded)
        public static Color32 ColorBytes(Color linear)
        {
            Color gamma = linear.gamma;
            return new Color32((byte)Mathf.RoundToInt(Mathf.Clamp01(gamma.r) * 255), (byte)Mathf.RoundToInt(Mathf.Clamp01(gamma.g) * 255), (byte)Mathf.RoundToInt(Mathf.Clamp01(gamma.b) * 255), 255);
        }
    }
}
