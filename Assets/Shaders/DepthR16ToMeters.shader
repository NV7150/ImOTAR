Shader "ImOTAR/DepthR16ToMeters"
{
    Properties
    {
        _MainTex ("Depth Texture (R16, mm)", 2D) = "white" {}
    }
    
    SubShader
    {
        Tags 
        { 
            "RenderType"="Opaque" 
            "Queue"="Geometry"
        }
        
        Pass
        {
            Name "R16mmToMeters"
            ZTest Always
            ZWrite Off
            Cull Off
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            
            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };
            
            sampler2D _MainTex;
            float4 _MainTex_ST;
            
            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }
            
            float frag (v2f i) : SV_Target
            {
                // R16テクスチャから深度値を取得（0-1に正規化済み）
                float normalizedDepth = tex2D(_MainTex, i.uv).r;
                
                // 0-1の正規化値を元のmm値に戻す: value * 65535
                float depthMM = normalizedDepth * 65535.0;
                
                // mmからメートルに変換: mm / 1000
                float depthMeters = depthMM / 1000.0;
                
                return depthMeters;
            }
            ENDCG
        }
    }
}
