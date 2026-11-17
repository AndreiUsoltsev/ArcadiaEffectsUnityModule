// Arcadia Effects: вывод готовой композиции (RenderTexture, премультиплицированная альфа,
// значения в гамма-пространстве — как у Canvas2D редактора) на квад в мире.
Shader "ArcadiaEffects/Display"
{
    Properties
    {
        [PerRendererData] _MainTex ("Composition", 2D) = "black" {}
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

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                #ifndef UNITY_COLORSPACE_GAMMA
                // композиция посчитана в гамме (как в браузере) — в линейном проекте переводим
                float a = max(c.a, 1e-5);
                c.rgb = GammaToLinearSpace(saturate(c.rgb / a)) * c.a;
                #endif
                float4 tint = i.color;
                return float4(c.rgb * tint.rgb, c.a) * tint.a;
            }
            ENDCG
        }
    }
}
