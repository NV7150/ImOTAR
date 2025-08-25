Shader "ImOTAR/ImuRotation"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Cull Off
            ZTest Always
            ZWrite Off
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // Unified property set
            float4x4 _R;           // full rotation matrix (3x3 part used)
            float _Fx, _Fy;        // intrinsics (unused here)
            float _Cx, _Cy;        // intrinsics principal point (unused here)
            float _PivotX, _PivotY; // rotation pivot in normalized UV

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                o.uv = float2((vertexID << 1) & 2, vertexID & 2);
                #if UNITY_REVERSED_Z
                o.positionCS = float4(o.uv * 2.0 - 1.0, 1.0, 1.0);
                #else
                o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
                #endif
                #if UNITY_UV_STARTS_AT_TOP
                o.positionCS.y = -o.positionCS.y;
                #endif
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                // Derive roll angle from rotation matrix _R (top-left 2x2)
                float3x3 R = (float3x3)_R;
                float angleRad = atan2(R[0][1], R[0][0]);
                float s = sin(angleRad);
                float c = cos(angleRad);

                float2 pivot = float2(_PivotX, _PivotY);
                float2 p = uv - pivot;
                float2 pr;
                pr.x =  c * p.x - s * p.y;
                pr.y =  s * p.x + c * p.y;
                float2 uvRot = pr + pivot;

                float depthVal = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvRot).r;
                return float4(depthVal, depthVal, depthVal, 1.0);
            }
            ENDHLSL
        }
    }
}


