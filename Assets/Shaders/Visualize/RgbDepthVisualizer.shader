Shader "ImOTAR/RgbDepthVisualizer"
{
    Properties
    {
        _MainTex ("RGB Base Image", 2D) = "white" {}
        _DepthTex ("Depth RT (RFloat)", 2D) = "white" {}
        _OverlayAlpha ("Depth Overlay Alpha", Range(0, 1)) = 0.5
        _Min ("Min (meters)", Float) = 0
        _Max ("Max (meters)", Float) = 5
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
            Name "RgbDepthVisualizer"
            ZTest [unity_GUIZTestMode]

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile __ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #define EPSILON 1e-6

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
            sampler2D _DepthTex;
            float4 _DepthTex_ST;
            float _OverlayAlpha;
            float _Min;
            float _Max;
            float4 _ClipRect;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.pos   = UnityObjectToClipPos(v.vertex);
                o.uv    = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;
                o.world = v.vertex;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // Branchless UI clip
                float2 p = i.world.xy;
                float clipMask = step(_ClipRect.x, p.x) * step(_ClipRect.y, p.y) * 
                                step(p.x, _ClipRect.z) * step(p.y, _ClipRect.w);
                clip(clipMask - 0.5);

                // Get RGB base image
                float4 baseColor = tex2D(_MainTex, i.uv);

                // Get depth value from RFloat
                float depth_m = tex2D(_DepthTex, TRANSFORM_TEX(i.uv, _DepthTex)).r;

                // Invalid depth check (depth_m < 0)
                float valid = step(0.0, depth_m);
                float4 invalidColor = float4(0, 0, 0, 0.5); // Semi-transparent black for invalid depth

                // Calculate depth visualization color (same as ColorTestVisualizer)
                float range = _Max - _Min;
                float percentile25 = _Min + range * 0.25;
                float percentile50 = _Min + range * 0.50;
                float percentile75 = _Min + range * 0.75;

                // Percentile comparisons
                float t25 = step(depth_m, percentile25);
                float t50 = step(depth_m, percentile50);
                float t75 = step(depth_m, percentile75);

                // Interval masks (no overlap)
                float mask25 = t25;
                float mask50 = t50 - t25;
                float mask75 = t75 - t50;
                float mask100 = 1.0 - t75;

                // Local normalized positions within each interval
                float local_t25 = saturate((depth_m - _Min) / max(percentile25 - _Min, EPSILON));
                float local_t50 = saturate((depth_m - percentile25) / max(percentile50 - percentile25, EPSILON));
                float local_t75 = saturate((depth_m - percentile50) / max(percentile75 - percentile50, EPSILON));
                float local_t100 = saturate((depth_m - percentile75) / max(_Max - percentile75, EPSILON));

                // Color calculation (branchless)
                float4 depthColor = mask25 * lerp(float4(1, 0, 0, 1), float4(1, 0.5, 0, 1), local_t25) +
                                   mask50 * lerp(float4(1, 0.5, 0, 1), float4(1, 1, 0, 1), local_t50) +
                                   mask75 * lerp(float4(1, 1, 0, 1), float4(0, 1, 0, 1), local_t75) +
                                   mask100 * lerp(float4(0, 1, 0, 1), float4(0, 0, 1, 1), local_t100);

                // Composite: base RGB + depth overlay
                float4 validResult = lerp(baseColor, depthColor, _OverlayAlpha);
                float4 invalidResult = lerp(baseColor, invalidColor, _OverlayAlpha);

                // Choose result based on depth validity
                float4 finalColor = lerp(invalidResult, validResult, valid);

                return finalColor * i.color;
            }
            ENDCG
        }
    }
}
