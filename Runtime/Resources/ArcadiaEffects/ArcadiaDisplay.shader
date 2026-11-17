// Arcadia Effects: показ эффекта в мире.
// Меш показа (ArcadiaEffectRenderer.DisplayMesh): вершины в пикселях композиции (Y вниз),
// в локальные юниты их переводит _Comp = (ширина, высота, 1/PPU). Сабмеши — холст композиции
// (премультиплицированная RenderTexture) и «прямые» слои, нарисованные без холста.
// Режим вершины (uv.z): 0 — премульт. текстура, 1 — тонировка (source-in), +2 — аддитивно (альфа 0).
// Цвета посчитаны в гамме, как в Canvas2D; в Linear-проекте переводятся в линейное пространство.
Shader "ArcadiaEffects/Display"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        ZWrite Off
        ZTest [_ZTest]
        Blend [_SrcBlend] [_DstBlend]

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _Comp; // ширина, высота композиции (px), 1/PPU

            struct appdata { float4 vertex : POSITION; float3 uv : TEXCOORD0; float4 color : TEXCOORD1; };
            struct v2f { float4 pos : SV_POSITION; float3 uv : TEXCOORD0; float4 color : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 local = float3((v.vertex.x - _Comp.x * 0.5) * _Comp.z, (_Comp.y * 0.5 - v.vertex.y) * _Comp.z, 0);
                o.pos = UnityObjectToClipPos(float4(local, 1));
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            float3 ToOut(float3 c)
            {
                #ifndef UNITY_COLORSPACE_GAMMA
                return GammaToLinearSpace(saturate(c));
                #else
                return c;
                #endif
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 t = tex2D(_MainTex, i.uv.xy);
                float mode = i.uv.z;
                bool add = mode > 1.5;
                bool tint = frac(mode * 0.5) > 0.25;
                float a = t.a * i.color.a;
                float3 rgb = tint ? i.color.rgb : (t.a > 1e-5 ? t.rgb / t.a : 0);
                float4 c = float4(ToOut(rgb) * a, add ? 0 : a);
                // _Color — свойство материала: Unity уже перевёл его в нужное пространство
                return float4(c.rgb * _Color.rgb, c.a) * _Color.a;
            }
            ENDCG
        }
    }
}
