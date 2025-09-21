Shader "ImOTAR/DepthClip"
{
    Properties
    {
        _MainTex ("Depth (meters)", 2D) = "white" {}
        _ClipDist ("Clip Distance (m)", Float) = 5
        _ClipEps ("Clip Epsilon (m)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            // Force point sampling regardless of RT filter mode
            SamplerState _PointClamp
            {
                Filter = MIN_MAG_MIP_POINT;
                AddressU = Clamp;
                AddressV = Clamp;
            };

            CBUFFER_START(UnityPerMaterial)
            float _ClipDist;
            float _ClipEps;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                o.uv = IN.uv;
                return o;
            }

            float frag(Varyings i) : SV_Target
            {
                float v = SAMPLE_TEXTURE2D_LOD(_MainTex, _PointClamp, i.uv, 0).r;
                float nanMask = 1.0 - step(0.0, abs(v - v));
                float clipMask = step(_ClipDist - _ClipEps, v);
                float mask = saturate(clipMask + nanMask);
                float outv = lerp(v, -1.0, mask);
                return outv;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
