Shader "ImOTAR/FullscreenDepthWriterTest"
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
            ColorMask RGBA

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

            struct FragmentOutput
            {
                float4 color : SV_Target;
                float depth : SV_Depth;
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

            FragmentOutput frag(Varyings i)
            {
                FragmentOutput output;
                
                float rawDepth = SAMPLE_TEXTURE2D(_DepthTex, sampler_DepthTex, i.uv).r; // Depth val in R channel
                if (rawDepth < 0)
                    discard;
                
                // 深度バッファへの書き込み用
                float nlDepth = ToNonLinerDepth(rawDepth);
                
                output.depth = nlDepth;
                
                // デバッグ用：深度値を赤色で可視化
                // rawDepthが小さい（近い）ほど明るい赤、大きい（遠い）ほど黒
                float depthVisualization = 1.0 - saturate(rawDepth);
                output.color = float4(depthVisualization, 0.0, 0.0, 1.0); // 赤色チャンネルのみ使用
                
                
                return output;
            }
            ENDHLSL
        }
    }
}