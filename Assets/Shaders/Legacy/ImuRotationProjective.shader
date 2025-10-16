Shader "ImOTAR/ImuRotationProjective"
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
            
            // Unified property set (match ImuRotation):
            float4x4 _R;          // full rotation matrix (3x3 part used)
            float _Fx, _Fy;       // intrinsics
            float _Cx, _Cy;       // intrinsics principal point
            float _PivotX, _PivotY; // unused here

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

                // Intrinsics (must be provided by script)
                float fx = _Fx;
                float fy = _Fy;
                float cx = _Cx;
                float cy = _Cy;

                // Use provided full rotation matrix _R (top-left 3x3)
                float3x3 R = (float3x3)_R;

                // Build K and K^-1 in normalized-uv domain
                float3x3 K = float3x3(
                    fx, 0.0, cx,
                    0.0, fy, cy,
                    0.0, 0.0, 1.0
                );
                float3x3 Kinv = float3x3(
                    1.0/fx, 0.0,   -cx/fx,
                    0.0,   1.0/fy, -cy/fy,
                    0.0,   0.0,     1.0
                );

                float3 p = float3(uv, 1.0);
                float3 q = mul(mul(K, R), mul(Kinv, p));
                float z = q.z;
                // Safe reciprocal to avoid dynamic branching
                float denom = max(abs(z), 1e-6);
                float2 uvSrc = q.xy / denom;

                // In-bounds masks using step (no dynamic if)
                float mx0 = step(0.0, uvSrc.x);
                float mx1 = step(uvSrc.x, 1.0);
                float my0 = step(0.0, uvSrc.y);
                float my1 = step(uvSrc.y, 1.0);
                float mz  = step(1e-6, z); // positive z only
                float m = mx0 * mx1 * my0 * my1 * mz;

                // Sample with clamped UV to keep valid fetch; blend with -1 for invalid
                float2 uvClamped = saturate(uvSrc);
                float sampleVal = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uvClamped).r;
                float depthVal = lerp(-1.0, sampleVal, m);
                return float4(depthVal, depthVal, depthVal, 1.0);
            }
            ENDHLSL
        }
    }
}


