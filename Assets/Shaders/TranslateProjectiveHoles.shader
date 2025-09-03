Shader "ImOTAR/TranslateProjectiveHoles"
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

            // Source texture to sample (previous PromptDA output at source time)
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize; // x=1/w, y=1/h

            // Current-frame depth (meters), already upscaled to target resolution
            TEXTURE2D(_DepthTex);
            SAMPLER(sampler_DepthTex);

            // SE(3) backwarp parameters (source->current)
            float4x4 _R;    // rotation matrix (use top-left 3x3)
            float3   _t;    // translation vector (meters), expressed in current camera frame
            float _Fx, _Fy; // intrinsics (normalized)
            float _Cx, _Cy; // intrinsics (normalized)

            // Controls
            float _UseNearest;        // 1: nearest (Load), 0: bilinear sample
            float _MaxUvDispN;        // max allowed |uv_s - uv_t| in normalized units; <=0 disables

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings o;
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
#if UNITY_REVERSED_Z
                o.positionCS = float4(uv * 2.0 - 1.0, 1.0, 1.0);
#else
                o.positionCS = float4(uv * 2.0 - 1.0, 0.0, 1.0);
#endif
#if UNITY_UV_STARTS_AT_TOP
                o.positionCS.y = -o.positionCS.y;
#endif
                o.uv = uv;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                // 1) Depth and validity mask
                float z = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, i.uv).r;
                float mDepth = step(1e-6, z);

                // 2) Intrinsics
                float3x3 K = float3x3(_Fx, 0.0, _Cx,
                                      0.0, _Fy, _Cy,
                                      0.0, 0.0, 1.0);
                float3x3 Kinv = float3x3(1.0/_Fx, 0.0,     -_Cx/_Fx,
                                         0.0,     1.0/_Fy, -_Cy/_Fy,
                                         0.0,     0.0,      1.0);

                // 3) Current pixel -> 3D in current camera
                float3 x = float3(i.uv, 1.0);
                float3 P_t = z * mul(Kinv, x);

                // 4) Transform to source frame
                float3x3 R3 = (float3x3)_R;
                float3x3 Rinv = transpose(R3);
                float3 P_s = mul(Rinv, (P_t - _t));
                float mPosZ = step(1e-6, P_s.z);

                // 5) Project to source uv
                float3 x_s = mul(K, P_s);
                float denom = max(abs(x_s.z), 1e-6);
                float2 uv_s = x_s.xy / denom;

                // 6) In-bounds mask
                float mx0 = step(0.0, uv_s.x);
                float mx1 = step(uv_s.x, 1.0);
                float my0 = step(0.0, uv_s.y);
                float my1 = step(uv_s.y, 1.0);
                float mBounds = mx0 * mx1 * my0 * my1;

                // 7) Displacement cutoff (branchless enable)
                float dispEnable = step(1e-6, _MaxUvDispN);
                float2 d = uv_s - i.uv;
                float dm = length(d);
                float mDispPass = step(dm, max(_MaxUvDispN, 1e-6));
                float mDisp = lerp(1.0, mDispPass, dispEnable);

                // 8) Sampling: mix bilinear and nearest by mask
                float bilinear = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv_s).r;
                float2 size = 1.0 / _MainTex_TexelSize.xy;
                int2 coord = (int2)round(uv_s * size - 0.5);
                coord.x = clamp(coord.x, 0, (int)size.x - 1);
                coord.y = clamp(coord.y, 0, (int)size.y - 1);
                float nearest = _MainTex.Load(int3(coord, 0)).r;
                float useNearest = step(0.5, _UseNearest);
                float sampleVal = lerp(bilinear, nearest, useNearest);

                // 9) Compose masks
                float m = mDepth * mPosZ * mBounds * mDisp;
                float outVal = lerp(-1.0, sampleVal, m);
                return float4(outVal, outVal, outVal, 1.0);
            }
            ENDHLSL
        }
    }
}




