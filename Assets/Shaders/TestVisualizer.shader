Shader "ImOTAR/TestVisualizer"
{
    Properties
    {
        _MainTex ("Depth RT (RFloat)", 2D) = "white" {}
        _Color   ("Tint", Color) = (1,1,1,1)
        _Min     ("Min (meters)", Float) = 0
        _Max     ("Max (meters)", Float) = 5
        [HideInInspector]_ClipRect("Clip Rect", Vector) = (-32767, -32767, 32767, 32767)
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "DepthGrayscale"
            ZTest [unity_GUIZTestMode]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata_t {
                float4 vertex : POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
            };

            struct v2f {
                float4 pos    : SV_POSITION;
                float4 color  : COLOR;
                float2 uv     : TEXCOORD0;
                float4 world  : TEXCOORD1;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Color;
            float  _Min;
            float  _Max;
            float4 _ClipRect;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.uv    = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color;
                o.world = v.vertex;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                #ifdef UNITY_UI_CLIP_RECT
                float2 p = i.world.xy;
                if (p.x < _ClipRect.x || p.y < _ClipRect.y || p.x > _ClipRect.z || p.y > _ClipRect.w)
                    discard;
                #endif

                // RFloat の R チャンネルに入っているメートル値を取得
                float depth_m = tex2D(_MainTex, i.uv).r;

                // Min-Max 正規化（0=黒, 1=白）
                float denom = max(1e-6, (_Max - _Min));
                float t = saturate((depth_m - _Min) / denom);
                float4 gray = float4(t, t, t, 1.0) * i.color;

                // depth_m < 0 を赤で可視化（分岐なし）
                float valid = step(0.0, depth_m);
                float4 red = float4(1.0, 0.0, 0.0, 1.0);
                return lerp(red, gray, valid);
            }
            ENDCG
        }
    }
}
