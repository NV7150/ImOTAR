Shader "ImOTAR/MaskViz"
{
    Properties
    {
        _MainTex ("Mask RT (RInt)", 2D) = "white" {}
        _Color   ("Tint", Color) = (1,1,1,1)
        _StaticCode ("Static Code", Int) = 1
        _DynamicCode ("Dynamic Code", Int) = 2
        _InvalidCode ("Invalid Code", Int) = -1
        _ColorStatic ("Static Color", Color) = (1,0,0,1)
        _ColorDynamic ("Dynamic Color", Color) = (0,0,1,1)
        _ColorInvalid ("Invalid Color", Color) = (0,0,0,1)
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
            Name "MaskColor"
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
            float4 _Color;
            int _StaticCode;
            int _DynamicCode;
            int _InvalidCode;
            float4 _ColorStatic;
            float4 _ColorDynamic;
            float4 _ColorInvalid;
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

                // UI clip

                // Fetch integer mask via Load (no filtering)
                int2 size = (int2)_MainTex_TexelSize.zw;
                int2 coord = (int2)floor(i.uv * (float2)size);
                coord = clamp(coord, int2(0,0), size - 1);
                int4 maskTexel = _MainTex.Load(int3(coord, 0));
                int mask = maskTexel.r;

                // 分岐回避でマスク値に応じた色を選択
                float eqDyn = step(0.5, 1.0 - abs((float)(mask - _DynamicCode)));
                float eqInv = step(0.5, 1.0 - abs((float)(mask - _InvalidCode)));
                float eqSta = saturate(1.0 - max(eqDyn, eqInv));

                float4 maskColor = _ColorStatic * eqSta + _ColorDynamic * eqDyn + _ColorInvalid * eqInv;
                return maskColor * i.color;
            }
            ENDCG
        }
    }
}
