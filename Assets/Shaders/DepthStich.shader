Shader "ImOTAR/DepthStich"
{
    Properties
    {
        _Src     ("Src (RFloat)", 2D) = "white" {}
        _Support ("Support (RFloat)", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One Zero

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _Src;
            sampler2D _Support;
            float4 _Src_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = TRANSFORM_TEX(v.uv, _Src);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float src = tex2D(_Src, i.uv).r;
                float sup = tex2D(_Support, i.uv).r;
                // branchless: if src < 0 -> use support
                float isInvalid = 1.0 - step(0.0, src);
                float d = lerp(src, sup, isInvalid);
                return float4(d, d, d, 1.0);
            }
            ENDCG
        }
    }
}


