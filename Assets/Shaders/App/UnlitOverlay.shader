Shader "ImOTAR/App/UnlitOverlay"
{
    Properties
    {
        _BaseColor ("Color", Color) = (1,1,1,1)
        _BaseMap ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags {
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Overlay"
            "RenderType"="Transparent"
            "IgnoreProjector"="True"
        }

        Pass
        {
            Name "UnlitOverlay"
            Cull Back
            ZTest Always
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _BaseMap_ST; // xy: tiling, zw: offset
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
                float4 color = albedo * _BaseColor; // texture × color tint
                return color;
            }
            ENDHLSL
        }
    }
}




