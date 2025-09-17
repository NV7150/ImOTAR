Shader "ImOTAR/DepthMixDebugViz"
{
    Properties
    {
        _MainTex ("Debug Mask (RInt)", 2D) = "white" {}
        _Tint    ("Tint", Color) = (1,1,1,1)
        _Alpha   ("Alpha", Range(0,1)) = 1

        _Code0 ("Code 0", Int) = 0
        _Code1 ("Code 1", Int) = 1
        _Code2 ("Code 2", Int) = 2
        _Code3 ("Code 3", Int) = 3
        _Code4 ("Code 4", Int) = 4

        _Color0 ("Color Code 0", Color) = (1,1,0,1)    // Yellow
        _Color1 ("Color Code 1", Color) = (1,0,1,1)    // Magenta
        _Color2 ("Color Code 2", Color) = (1,0,0,1)    // Red
        _Color3 ("Color Code 3", Color) = (0,1,1,1)    // Cyan
        _Color4 ("Color Code 4", Color) = (0,0,0,1)    // Black
        _ColorOther ("Color Other", Color) = (0.5,0.5,0.5,1)

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
            Name "MaskViz"
            ZTest [unity_GUIZTestMode]

            CGPROGRAM
            #pragma target 4.0
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

            // Integer mask texture (R32_SInt)
            Texture2D<int> _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize; // xy = 1/size, zw = size
            float4 _Tint;
            float _Alpha;
            int _Code0, _Code1, _Code2, _Code3, _Code4;
            float4 _Color0, _Color1, _Color2, _Color3, _Color4;
            float4 _ColorOther;
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
                #ifdef UNITY_UI_CLIP_RECT
                float2 p = i.world.xy;
                if (p.x < _ClipRect.x || p.y < _ClipRect.y || p.x > _ClipRect.z || p.y > _ClipRect.w)
                    discard;
                #endif

                int2 size = (int2)_MainTex_TexelSize.zw;
                int2 coord = (int2)floor(i.uv * (float2)size);
                coord = clamp(coord, int2(0,0), size - 1);
                int mask = _MainTex.Load(int3(coord, 0)).r;

                // eq(mask, codeX) in branchless form
                float eq0 = step(0.5, 1.0 - abs((float)(mask - _Code0)));
                float eq1 = step(0.5, 1.0 - abs((float)(mask - _Code1)));
                float eq2 = step(0.5, 1.0 - abs((float)(mask - _Code2)));
                float eq3 = step(0.5, 1.0 - abs((float)(mask - _Code3)));
                float eq4 = step(0.5, 1.0 - abs((float)(mask - _Code4)));
                float any = saturate(eq0 + eq1 + eq2 + eq3 + eq4);

                float4 col = _ColorOther * (1.0 - any)
                           + _Color0 * eq0
                           + _Color1 * eq1
                           + _Color2 * eq2
                           + _Color3 * eq3
                           + _Color4 * eq4;

                col *= _Tint;
                col.a *= _Alpha;
                return col;
            }
            ENDCG
        }
    }
}


