Shader "Unlit/RegionIdDebug"
{
    Properties
    {
        _MainTex ("RegionID (RGBA32 packed)", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ColorCount ("Color Count", Range(2, 32)) = 8
        _BoundaryColor ("Boundary Color (ID==0)", Color) = (0,0,0,1)
        _Saturation ("Saturation", Range(0,1)) = 1
        _Value ("Value", Range(0,1)) = 1
        [HideInInspector]_ClipRect("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)
    }
    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }
        LOD 100
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "RegionID_UI"
            ZTest [unity_GUIZTestMode]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float4 _ClipRect;
            float _ColorCount;
            float4 _BoundaryColor;
            float _Saturation;
            float _Value;

            struct appdata {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f {
                float2 uv     : TEXCOORD0;
                float4 vertex : SV_POSITION;
                float4 color  : COLOR;
                float4 world  : TEXCOORD1;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color;
                o.world = v.vertex;
                return o;
            }

            uint decodeID(float4 c)
            {
                // c in 0..1. Convert to 0..255 and pack little-endian
                uint r = (uint)round(saturate(c.r) * 255.0);
                uint g = (uint)round(saturate(c.g) * 255.0);
                uint b = (uint)round(saturate(c.b) * 255.0);
                uint a = (uint)round(saturate(c.a) * 255.0);
                return (r) | (g << 8) | (b << 16) | (a << 24);
            }

            float3 hsv2rgb(float3 hsvc)
            {
                float H = hsvc.x; // 0..1
                float S = hsvc.y;
                float V = hsvc.z;
                float3 rgb = V * (1 - S) * float3(1,1,1);
                float h6 = H * 6.0;
                float f = frac(h6);
                float p = V * (1.0 - S);
                float q = V * (1.0 - S * f);
                float t = V * (1.0 - S * (1.0 - f));
                uint i = (uint)floor(h6);
                if (i == 0) rgb = float3(V, t, p);
                else if (i == 1) rgb = float3(q, V, p);
                else if (i == 2) rgb = float3(p, V, t);
                else if (i == 3) rgb = float3(p, q, V);
                else if (i == 4) rgb = float3(t, p, V);
                else rgb = float3(V, p, q);
                return rgb;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                #ifdef UNITY_UI_CLIP_RECT
                float2 p = i.world.xy;
                if (p.x < _ClipRect.x || p.y < _ClipRect.y || p.x > _ClipRect.z || p.y > _ClipRect.w)
                    discard;
                #endif

                float4 idPacked = tex2D(_MainTex, i.uv);
                uint id = decodeID(idPacked);

                float4 outCol;
                if (id == 0u)
                {
                    outCol = _BoundaryColor;
                }
                else
                {
                    uint colors = (uint)max(2.0, _ColorCount);
                    uint idx = id % colors;
                    float hue = (colors > 0) ? ( (float)idx / (float)colors ) : 0.0;
                    float3 rgb = hsv2rgb(float3(hue, _Saturation, _Value));
                    outCol = float4(rgb, 1.0);
                }

                // Apply UI vertex/material tint
                outCol *= i.color;
                return outCol;
            }
            ENDCG
        }
    }
}


