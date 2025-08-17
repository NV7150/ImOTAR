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
                float rawDepth = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, i.uv).r; // Depth val in R channel
                if (rawDepth < 0)
                    discard;
                

                float nlDepth = ToNonLinerDepth(rawDepth);
                
                return nlDepth;
            }
            ENDHLSL
        }
    }
}