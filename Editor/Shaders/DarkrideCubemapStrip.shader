// Resamples a cubemap into darkride's vertical strip (faces +X -X +Y -Y +Z -Z from the top, each FaceSize square).
// The engine mirrors the X and Z faces left to right and the Y faces top to bottom when it reads the strip, then
// uploads them as OpenGL cube faces, which it samples by world direction; darkride's X is Unity's mirrored.
Shader "Hidden/Darkride/CubemapStrip"
{
    Properties
    {
        _Cube ("Cubemap", CUBE) = "" {}
        _FaceSize ("Face size", Float) = 512
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            samplerCUBE _Cube;
            float _FaceSize;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float height = _FaceSize * 6.0;
                float yFromTop = (1.0 - i.uv.y) * height;
                float px = i.uv.x * _FaceSize;
                int face = clamp((int)floor(yFromTop / _FaceSize), 0, 5);
                float py = yFromTop - face * _FaceSize;
                bool yFace = face == 2 || face == 3;
                float col = yFace ? px : (_FaceSize - px);
                float row = yFace ? (_FaceSize - py) : py;
                float a = -1.0 + 2.0 * col / _FaceSize;
                float b = 1.0 - 2.0 * row / _FaceSize;
                float3 d;
                if (face == 0) d = float3(1, b, -a);
                else if (face == 1) d = float3(-1, b, a);
                else if (face == 2) d = float3(a, 1, -b);
                else if (face == 3) d = float3(a, -1, b);
                else if (face == 4) d = float3(a, b, 1);
                else d = float3(-a, b, -1);
                d.x = -d.x;
                return float4(texCUBElod(_Cube, float4(d, 0)).rgb, 1);
            }
            ENDCG
        }
    }
}
