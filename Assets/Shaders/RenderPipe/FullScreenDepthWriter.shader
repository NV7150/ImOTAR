Shader "ImOTAR/FullscreenDepthWriter"
{
    
    SubShader
    {
        Tags {
            "RenderPipeline"="UniversalPipeline"
//            "Queue"="Geometry-1"
//            "LightMode" = "DepthOnly" 
        }
        Pass
        {
            Cull Off
            Blend One Zero
            ZTest Always
            ZWrite On
            ColorMask 0 

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            
            TEXTURE2D(_DepthTex);
            SAMPLER(sampler_DepthTex);

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // メートル深度を非線形Z bufferに戻す関数
            inline float ToNonLinerDepth(float depth)
            {
                // LiDAR距離を0-1の線形深度に変換
                float near = _ProjectionParams.y;
                float far = _ProjectionParams.z;

                float linearDepth = (depth - near) / (far - near);

                linearDepth = saturate(linearDepth);

                // #if UNITY_REVERSED_Z
                // #else
                //     linearDepth = 1.0 - linearDepth;
                // #endif
                
                float depthNl = (1.0 - linearDepth * _ZBufferParams.y) / (linearDepth * _ZBufferParams.x);

                return depthNl;
            }

            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings o;

                // 頂点IDに基づいてUV座標を生成
                // (0,0), (2,0), (0,2) というUVを作る
                o.uv = float2((vertexID << 1) & 2, vertexID & 2);

                // UVからクリップスペース座標を生成
                // (0,0) -> (-1,-1)
                // (2,0) -> ( 3,-1)
                // (0,2) -> (-1, 3)
                #if UNITY_REVERSED_Z
                o.positionCS = float4(o.uv * 2.0 - 1.0, 1.0, 1.0); // Near = 0
                #else
                o.positionCS = float4(o.uv * 2.0 - 1.0, 0.0, 1.0); // Far = 1
                #endif

                // DirectX環境ではYが反転するので、ここで修正する
                #if UNITY_UV_STARTS_AT_TOP
                o.positionCS.y = -o.positionCS.y;
                #endif

                return o;
            }

            float frag(Varyings i) : SV_Depth
            {
                // Valid-only bilinear interpolation
                uint tw, th;
                _DepthTex.GetDimensions(tw, th);
                float2 texSize = float2(tw, th);

                // map uv to pixel space (texel center at n+0.5)
                float2 uvPix = i.uv * texSize - 0.5;
                int2  p0 = (int2)floor(uvPix);
                float2 f = frac(uvPix);

                int2 maxIdx = int2((int)tw - 1, (int)th - 1);
                int2 p00 = clamp(p0 + int2(0, 0), int2(0,0), maxIdx);
                int2 p10 = clamp(p0 + int2(1, 0), int2(0,0), maxIdx);
                int2 p01 = clamp(p0 + int2(0, 1), int2(0,0), maxIdx);
                int2 p11 = clamp(p0 + int2(1, 1), int2(0,0), maxIdx);

                float2 uv00 = (float2(p00) + 0.5) / texSize;
                float2 uv10 = (float2(p10) + 0.5) / texSize;
                float2 uv01 = (float2(p01) + 0.5) / texSize;
                float2 uv11 = (float2(p11) + 0.5) / texSize;

                float d00 = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, uv00).r;
                float d10 = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, uv10).r;
                float d01 = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, uv01).r;
                float d11 = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, uv11).r;

                // Treat negative depths as invalid; zero remains valid per project policy
                float m00 = step(0.0, d00);
                float m10 = step(0.0, d10);
                float m01 = step(0.0, d01);
                float m11 = step(0.0, d11);

                float w00 = (1.0 - f.x) * (1.0 - f.y) * m00;
                float w10 = (       f.x) * (1.0 - f.y) * m10;
                float w01 = (1.0 - f.x) * (       f.y) * m01;
                float w11 = (       f.x) * (       f.y) * m11;

                float denom = w00 + w10 + w01 + w11;
                if (denom <= 0.0) discard;

                float rawDepth = (d00*w00 + d10*w10 + d01*w01 + d11*w11) / denom;
                if (rawDepth <= 0.0) discard;

                float nlDepth = ToNonLinerDepth(rawDepth);
                return nlDepth;
            }
            ENDHLSL
        }
    }
}