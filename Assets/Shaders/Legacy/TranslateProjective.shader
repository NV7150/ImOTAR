Shader "ImOTAR/TranslateProjective"
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

            // Source texture to sample (previous PromptDA output)
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // Current-frame depth (meters), already upscaled to target resolution
            TEXTURE2D(_DepthTex);
            SAMPLER(sampler_DepthTex);

            // SE(3) backwarp parameters
            float4x4 _R;    // rotation matrix (use top-left 3x3), mapping source->current
            float3   _t;    // translation vector (meters), source->current, expressed in current camera frame
            float _Fx, _Fy; // intrinsics (normalized)
            float _Cx, _Cy; // intrinsics (normalized)

            // No display transform: assume inputs are already in screen-aligned UV (no extra flip)

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
                // Read current-frame depth (meters) in screen UV (inputs are already correctly oriented)
                float z = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, i.uv).r;
                // Invalid depth guard
                if (z <= 0.0) {
                    return float4(-1.0, -1.0, -1.0, 1.0);
                }

                // Build intrinsics in normalized-uv domain and its inverse
                float3x3 K = float3x3(_Fx, 0.0, _Cx,
                                      0.0, _Fy, _Cy,
                                      0.0, 0.0, 1.0);
                float3x3 Kinv = float3x3(1.0/_Fx, 0.0,     -_Cx/_Fx,
                                         0.0,     1.0/_Fy, -_Cy/_Fy,
                                         0.0,     0.0,      1.0);

                // Target pixel to current camera 3D point
                float3 x = float3(i.uv, 1.0);
                float3 P_t = z * mul(Kinv, x);

                // Transform to source camera frame: P_s = R^{-1} (P_t - t)
                float3x3 R3 = (float3x3)_R;
                // R_inv = transpose(R)  if R is pure rotation
                float3x3 Rinv = transpose(R3);
                float3 P_s = mul(Rinv, (P_t - _t));

                // Project to source image uv
                float3 x_s = mul(K, P_s);
                float denom = max(abs(x_s.z), 1e-6);
                float2 uv_s = x_s.xy / denom;
                // Mask invalid reprojection (outside image or behind camera)
                if (uv_s.x < 0.0 || uv_s.x > 1.0 || uv_s.y < 0.0 || uv_s.y > 1.0 || x_s.z <= 0.0) {
                    return float4(-1.0, -1.0, -1.0, 1.0);
                }

                float val = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv_s).r;
                return float4(val, val, val, 1.0);
            }
            ENDHLSL
        }
    }
}


