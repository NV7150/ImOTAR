Shader "ImOTAR/ColorTestVisualizer"
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
            Name "ColorDepthVisualizer"
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

                // 無効値チェック（depth_m < 0 を黒で表示）
                float valid = step(0.0, depth_m);
                float4 invalidColor = float4(0, 0, 0, 1); // 無効深度は黒

                // Min-Maxからパーセンタイルを自動計算
                float range = _Max - _Min;
                float percentile25 = _Min + range * 0.25;
                float percentile50 = _Min + range * 0.50;
                float percentile75 = _Min + range * 0.75;

                // パーセンタイルとの比較
                float t25 = step(depth_m, percentile25);
                float t50 = step(depth_m, percentile50);
                float t75 = step(depth_m, percentile75);

                // 区間マスク（重複なし）
                float mask25 = t25;
                float mask50 = t50 - t25;
                float mask75 = t75 - t50;
                float mask100 = 1.0 - t75;

                // 各区間内での正規化位置
                float local_t25 = saturate((depth_m - _Min) / (percentile25 - _Min));
                float local_t50 = saturate((depth_m - percentile25) / (percentile50 - percentile25));
                float local_t75 = saturate((depth_m - percentile50) / (percentile75 - percentile50));
                float local_t100 = saturate((depth_m - percentile75) / (_Max - percentile75));

                // 色計算（分岐なし）
                float4 validColor = mask25 * lerp(float4(1, 0, 0, 1), float4(1, 0.5, 0, 1), local_t25) +
                                   mask50 * lerp(float4(1, 0.5, 0, 1), float4(1, 1, 0, 1), local_t50) +
                                   mask75 * lerp(float4(1, 1, 0, 1), float4(0, 1, 0, 1), local_t75) +
                                   mask100 * lerp(float4(0, 1, 0, 1), float4(0, 0, 1, 1), local_t100);

                // 有効深度は計算された色、無効深度は黒
                return lerp(invalidColor, validColor, valid) * i.color;
            }
            ENDCG
        }
    }
}
