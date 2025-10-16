Shader "ImOTAR/SplatElipseTransform" {
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
            float4x4 _Proj; // camera projection built from intrinsics & near/far

            int _RenderMode; // 0: valid only (r>=0), 1: holes only (r<0)

            struct VSOut {
                float4 posCS : SV_Position;
                float depthM : TEXCOORD0; // meters
                float2 local : TEXCOORD1; // quad local coords in [-1,1]
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

                // Early reject based on render mode (minimal branching)
                bool isHole = (r < 0.0);
                if ((_RenderMode == 0 && isHole) || (_RenderMode == 1 && !isHole)){
                    // send off-screen
                    o.posCS = float4(0, 0, 0, 0);
                    o.depthM = -1.0;
                    o.local = float2(0.0, 0.0);
                    return o;
                }

                // Transform to current camera frame
                float3 x0 = p.xyz;
                float3 x1 = mul((float3x3)_R, x0) + _t;

                // compute pixel radius per axis
                float r_px_x = (r * _FxPx) / max(x1.z, 1e-6);
                float r_px_y = (r * _FyPx) / max(x1.z, 1e-6);

                // project to pixel center with axis-specific radius
                float u = (_FxPx * x1.x / x1.z) + _CxPx + c.x * r_px_x * 0.5;
                float v = (_FyPx * x1.y / x1.z) + _CyPx + c.y * r_px_y * 0.5;

                // convert to NDC and then clip using custom projection
                // Build a clip-space position that matches given pixel pos using _Proj
                // Inverse of usual path: we form a camera-space position for the quad corner and multiply by _Proj
                // Recover camera-space from pixel offsets at given depth
                float X = (u - _CxPx) * x1.z / _FxPx;
                float Y = (v - _CyPx) * x1.z / _FyPx;
                float4 cam = float4(X, Y, x1.z, 1.0);
                o.posCS = mul(_Proj, cam);
                o.depthM = x1.z;
                o.local = c;
                return o;
            }

            float4 Frag(VSOut i) : SV_Target {

                // hard ellipse mask (unit circle in local quad, appears as ellipse in screen space)
                float r2 = dot(i.local, i.local);
                clip(1.0 - r2);

                return float4(i.depthM, 0, 0, 1);
            }
            ENDHLSL
        }
    }
}






