// Arcadia Effects: отрисовка спрайтов/частиц/лент в RenderTexture композиции.
// Координаты вершин — пиксели композиции (Y вниз), проекция — _AfxProj.
// Цвет на выходе ПРЕМУЛЬТИПЛИЦИРОВАННЫЙ (как в Canvas2D), режим наложения задаётся
// свойствами материала (_SrcC/_DstC/_SrcA/_DstA); штрихи трейлов — через буфер глубины.
Shader "Hidden/ArcadiaEffects/Draw"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _SrcC ("Src Color", Float) = 1
        _DstC ("Dst Color", Float) = 10
        _SrcA ("Src Alpha", Float) = 1
        _DstA ("Dst Alpha", Float) = 10
        _ColorMask ("Color Mask", Float) = 15
        _StencilRef ("Stencil Ref", Float) = 0
        _StencilCompF ("Stencil Comp Front", Float) = 8
        _StencilPassF ("Stencil Pass Front", Float) = 0
        _StencilCompB ("Stencil Comp Back", Float) = 8
        _StencilPassB ("Stencil Pass Back", Float) = 0
        _ZTest ("ZTest", Float) = 8
        _ZWrite ("ZWrite", Float) = 0
        _UseDepth ("Use Depth", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off
        ZWrite [_ZWrite]
        ZTest [_ZTest]
        Blend [_SrcC] [_DstC], [_SrcA] [_DstA]
        ColorMask [_ColorMask]
        Stencil
        {
            Ref [_StencilRef]
            ReadMask 255
            WriteMask 255
            CompFront [_StencilCompF]
            PassFront [_StencilPassF]
            CompBack [_StencilCompB]
            PassBack [_StencilPassB]
        }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4x4 _AfxProj;
            float _UseDepth;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 uv : TEXCOORD0;     // z — режим: 0 премульт. текстура, 1 тонировка (source-in)
                float4 color : TEXCOORD1;  // rgb — цвет тонировки 0..1, a — непрозрачность
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 w = mul(unity_ObjectToWorld, float4(v.vertex.xy, 0, 1));
                o.pos = mul(_AfxProj, float4(w.xy, 0, 1));
                if (_UseDepth > 0.5)
                {
                    // ШТРИХИ ТРЕЙЛОВ: у каждого штриха своя глубина (vertex.z: 1 — дальше, меньше — ближе).
                    // ZTest Less + ZWrite: пиксель одного штриха закрашивается один раз (как контур
                    // Canvas2D), а следующий штрих — ближе и проходит. Unity сам переворачивает
                    // сравнение на платформах с обратным Z, поэтому глубину пишем «как есть».
                    float d = saturate(v.vertex.z);
                    #if UNITY_REVERSED_Z
                    o.pos.z = (1.0 - d) * o.pos.w;
                    #else
                    o.pos.z = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, d) * o.pos.w;
                    #endif
                }
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv.xy);
                float4 col = i.uv.z > 0.5 ? float4(i.color.rgb * c.a, c.a) : c;
                return col * i.color.a;
            }
            ENDCG
        }
    }
}
