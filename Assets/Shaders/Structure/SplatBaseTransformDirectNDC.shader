Shader "ImOTAR/SplatBaseTransformDirectNDC" {
    Properties {
        // Depth test mode controlled from C# (default: LEqual)
        [Enum(CompareFunction)] _ZTest ("ZTest", Int) = 4
    }
    SubShader {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass {
            Name "SplatPass"
            ZTest [_ZTest]
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            StructuredBuffer<float4> _Points; // xyz cam-space [m], w radius [m] (holes: w < 0)

            float4x4 _R; // source->current rotation (3x3 used)
            float3 _t;   // source->current translation (current frame)

            float _FxPx, _FyPx, _CxPx, _CyPx;
            int _Width, _Height;
            float _Near, _Far; // near/far planes for depth calculation

            int _RenderMode; // 0: valid only (r>=0), 1: holes only (r<0)

            struct VSOut {
                float4 posCS : SV_Position;
                float depthM : TEXCOORD0; // meters
            };

            // map vertexID -> (pointIndex, corner)
            static const float2 corners[6] = {
                float2(-1.0, -1.0), float2(1.0, -1.0), float2(1.0, 1.0),
                float2(-1.0, -1.0), float2(1.0, 1.0), float2(-1.0, 1.0)
            };

            VSOut Vert(uint vid : SV_VertexID){
                VSOut o;
                uint pointIndex = vid / 6;
                uint cornerIndex = vid - pointIndex * 6;
                float2 c = corners[cornerIndex];

                float4 p = _Points[pointIndex];
                float r = p.w;

                // Branchless selection mask: draw valid when _RenderMode==0, draw holes when _RenderMode==1
                // holeMask = 1 when r<0, else 0
                float holeMask = 1.0 - step(0.0, r);
                float validMask = 1.0 - holeMask;
                float modeHole = saturate((float)_RenderMode); // 0 or 1
                float drawMask = lerp(validMask, holeMask, modeHole); // 1=draw, 0=skip

                // Transform to current camera frame
                float3 x0 = p.xyz;
                float3 x1 = mul((float3x3)_R, x0) + _t;

                // compute pixel radius per axis
                float r_px_x = (r * _FxPx) / max(x1.z, 1e-6);
                float r_px_y = (r * _FyPx) / max(x1.z, 1e-6);

                // project to pixel center with axis-specific radius
                float u = (_FxPx * x1.x / x1.z) + _CxPx + c.x * r_px_x * 0.5;
                float v = (_FyPx * x1.y / x1.z) + _CyPx + c.y * r_px_y * 0.5;

                // Direct NDC conversion (no projection matrix)
                // Y-axis flip to match GL.GetGPUProjectionMatrix(P, true) behavior
                float ndcX = (2.0 * u / (float)_Width) - 1.0;
                float ndcY = 1.0 - (2.0 * v / (float)_Height);
                
                // Compute NDC depth - inverted to match inverted ZTest logic in PcdCorrector.cs
                float n = _Near;
                float f = _Far;
                float z = x1.z;
                float clipZ = (f / (f - n)) * z + ((-f * n) / (f - n));
                float ndcZ = clipZ / z;
                
                #if UNITY_REVERSED_Z
                    // ReversedZ but using LessEqual (inverted) -> use normal Z calculation (no change)
                #else
                    // NormalZ but using GreaterEqual (inverted) -> invert depth
                    ndcZ = 1.0 - ndcZ;
                #endif

                float4 pos = float4(ndcX, ndcY, ndcZ, 1.0);

                // Push non-drawn primitives fully outside clip rect (keep w>0)
                static const float OFF_NDC = 2.0;
                float4 offPos = float4(OFF_NDC, OFF_NDC, 1.0, 1.0);
                o.posCS = lerp(offPos, pos, drawMask);
                o.depthM = lerp(-1.0, x1.z, drawMask);
                return o;
            }

            float4 Frag(VSOut i) : SV_Target {

                return float4(i.depthM, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}

