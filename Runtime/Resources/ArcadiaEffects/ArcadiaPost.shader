// Arcadia Effects: полноэкранные проходы композиции (все цвета премультиплицированы).
// Вершины — пиксели композиции; фрагмент получает свою координату композиции (cpos).
//   0 — радиальное затухание слоя (destination-in)
//   1 — гауссов блюр по оси (прозрачная граница, как CSS blur)
//   2 — турбулентное искажение (postfx displace)
//   3 — пиксель-арт: выборка блока + альфа + цвет (результат — прямая альфа)
//   4 — пиксель-арт: обводка (результат — премультиплицированный)
Shader "Hidden/ArcadiaEffects/Post"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Grid ("Grid", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    float4x4 _AfxProj;
    // xy — размер координатного пространства цели (px композиции), zw — масштаб RT (px RT на px композиции)
    float4 _Coord;

    struct appdata { float4 vertex : POSITION; };
    struct v2f { float4 pos : SV_POSITION; float2 cpos : TEXCOORD0; };

    v2f vert(appdata v)
    {
        v2f o;
        float4 w = mul(unity_ObjectToWorld, float4(v.vertex.xy, 0, 1));
        o.pos = mul(_AfxProj, float4(w.xy, 0, 1));
        o.cpos = w.xy;
        return o;
    }

    // тексель источника по целочисленному пикселю (x вправо, y вниз от верхнего края)
    float4 TexelAt(float2 p)
    {
        float2 sz = _MainTex_TexelSize.zw;
        if (p.x < 0 || p.y < 0 || p.x >= sz.x || p.y >= sz.y) return 0;
        float2 uv = float2((p.x + 0.5) / sz.x, 1.0 - (p.y + 0.5) / sz.y);
        return tex2Dlod(_MainTex, float4(uv, 0, 0));
    }
    ENDCG

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Cull Off ZWrite Off ZTest Always

        // 0: радиальное затухание — dst *= mask
        Pass
        {
            Blend Zero SrcAlpha, Zero SrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 _Fade; // xy — центр (px композиции), z — внутренний радиус, w — внешний
            float _FadeKK;
            float4 frag(v2f i) : SV_Target
            {
                float2 d = i.cpos - _Fade.xy;
                d.y /= _FadeKK;
                float dist = length(d);
                float a;
                if (_Fade.w <= _Fade.z) a = dist < _Fade.w ? 1 : 0;   // жёсткий срез
                else
                {
                    float t = saturate((dist - _Fade.z) / (_Fade.w - _Fade.z));
                    a = t < 0.55 ? lerp(1, 0.5, t / 0.55) : lerp(0.5, 0, (t - 0.55) / 0.45);
                }
                return float4(0, 0, 0, a);
            }
            ENDCG
        }

        // 1: гауссов блюр по оси _BlurDir (пиксели RT)
        Pass
        {
            Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float2 _BlurDir;
            float _Sigma;
            float4 frag(v2f i) : SV_Target
            {
                float2 p = floor(i.cpos * _Coord.zw);
                int r = (int)ceil(_Sigma * 3);
                r = min(r, 48);
                float inv = 1.0 / (2 * _Sigma * _Sigma);
                float4 sum = 0;
                float ws = 0;
                [loop]
                for (int k = -r; k <= r; k++)
                {
                    float w = exp(-(k * k) * inv);
                    sum += TexelAt(p + _BlurDir * k) * w;
                    ws += w;
                }
                return sum / ws;
            }
            ENDCG
        }

        // 2: турбулентное искажение. _Grid — смещения (px композиции) на редкой сетке шагом _GridStep
        Pass
        {
            Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            sampler2D _Grid;
            float4 _Grid_TexelSize;
            float _GridStep;

            float2 GridAt(int gx, int gy)
            {
                float2 uv = float2((gx + 0.5) * _Grid_TexelSize.x, (gy + 0.5) * _Grid_TexelSize.y);
                return tex2Dlod(_Grid, float4(uv, 0, 0)).rg;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 P = floor(i.cpos * _Coord.zw);          // пиксель RT
                float2 c = P / _Coord.zw;                      // он же в px композиции
                float2 g = c / _GridStep;
                int i0 = (int)floor(g.x), j0 = (int)floor(g.y);
                float tx0 = g.x - i0, ty0 = g.y - j0;
                float fx = tx0 * tx0 * (3 - 2 * tx0);
                float fy = ty0 * ty0 * (3 - 2 * ty0);
                float2 a00 = GridAt(i0, j0), a10 = GridAt(i0 + 1, j0);
                float2 a01 = GridAt(i0, j0 + 1), a11 = GridAt(i0 + 1, j0 + 1);
                float2 d = lerp(lerp(a00, a10, fx), lerp(a01, a11, fx), fy);

                float2 s = P + d * _Coord.zw;
                float2 f = floor(s);
                float tx = s.x - f.x, ty = s.y - f.y;
                float2 sz = _MainTex_TexelSize.zw;
                if (f.x < -1 || f.y < -1 || f.x >= sz.x || f.y >= sz.y) return 0;
                // премультиплицированные тапы = билинейная выборка без тёмной каймы
                return TexelAt(f) * (1 - tx) * (1 - ty) + TexelAt(f + float2(1, 0)) * tx * (1 - ty)
                     + TexelAt(f + float2(0, 1)) * (1 - tx) * ty + TexelAt(f + float2(1, 1)) * tx * ty;
            }
            ENDCG
        }

        // 3: пиксель-арт — выборка блока, альфа, цвет. Цель — сетка арта gw x gh (1 px = 1 ячейка)
        Pass
        {
            Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 _PxGrid;     // bx, by, ox, oy (px композиции)
            float _PxScale;     // px RT на px композиции у источника
            float _PxSample;    // 0 peak, 1 box, 2 point
            float _PxGain;
            float _PxAlpha;     // 0 cut, 1 steps, 2 soft
            float _PxThr, _PxALev;
            float _PxColor;     // 0 none, 1 levels, 2 palette, 3 ramp
            float _PxLev, _PxRampN, _PxSat, _PxCon;
            float _PxDither, _PxDitherAmt;
            float _PalN;
            float4 _Pal[64];
            float4 _Ramp[32];
            float _Bayer[64];

            float4 Straight(float4 c) { return c.a > 0 ? float4(c.rgb / c.a, c.a) : 0; }

            float4 frag(v2f i) : SV_Target
            {
                int cx = (int)floor(i.cpos.x), cy = (int)floor(i.cpos.y);
                float2 sz = _MainTex_TexelSize.zw;
                int x0 = (int)max(0, floor((_PxGrid.z + cx * _PxGrid.x) * _PxScale + 0.5));
                int x1 = (int)min(sz.x, floor((_PxGrid.z + (cx + 1) * _PxGrid.x) * _PxScale + 0.5));
                int y0 = (int)max(0, floor((_PxGrid.w + cy * _PxGrid.y) * _PxScale + 0.5));
                int y1 = (int)min(sz.y, floor((_PxGrid.w + (cy + 1) * _PxGrid.y) * _PxScale + 0.5));

                float4 s = 0; // прямая альфа, 0..255
                if (_PxSample < 0.5)
                {
                    float ba = -1, bl = -1;
                    [loop] for (int y = y0; y < min(y1, y0 + 96); y++)
                    {
                        [loop] for (int x = x0; x < min(x1, x0 + 96); x++)
                        {
                            float4 c = round(Straight(TexelAt(float2(x, y))) * 255);
                            if (c.a < ba) continue;
                            float l = c.r * 299 + c.g * 587 + c.b * 114;
                            if (c.a > ba || l > bl) { ba = c.a; bl = l; s = c; }
                        }
                    }
                    if (ba <= 0) s = 0;
                }
                else if (_PxSample < 1.5)
                {
                    float4 acc = 0; float n = 0;
                    [loop] for (int y = y0; y < min(y1, y0 + 96); y++)
                    {
                        [loop] for (int x = x0; x < min(x1, x0 + 96); x++) { acc += TexelAt(float2(x, y)); n += 1; }
                    }
                    s = n > 0 ? round(Straight(acc / n) * 255) : 0;
                }
                else
                {
                    float2 c = float2(_PxGrid.z + (cx + 0.5) * _PxGrid.x, _PxGrid.w + (cy + 0.5) * _PxGrid.y) * _PxScale;
                    s = round(Straight(TexelAt(floor(c))) * 255);
                }

                float a = s.a / 255;
                int bn = (int)_PxDither;
                float dd = bn > 0 ? _Bayer[(cy % bn) * bn + (cx % bn)] * _PxDitherAmt : 0;
                if (_PxGain != 1) a *= _PxGain;
                a = min(a, 1);
                float aq = _PxALev - 1;
                if (_PxAlpha < 0.5) a = (a + dd) >= _PxThr ? 1 : 0;
                else if (_PxAlpha < 1.5) a = saturate(floor((a + dd / aq) * aq + 0.5) / aq);
                if (a <= 0) return 0;
                a = round(a * 255) / 255;
                float3 c = s.rgb;
                if (!(_PxColor < 0.5 && _PxSat == 1 && _PxCon == 1))
                {
                    if (_PxSat != 1 || _PxCon != 1)
                    {
                        float lum = dot(c, float3(0.299, 0.587, 0.114));
                        c = lum + (c - lum) * _PxSat;
                        if (_PxCon != 1) c = 128 + (c - 128) * _PxCon;
                        c = clamp(c, 0, 255);
                    }
                    if (_PxColor > 0.5 && _PxColor < 1.5)
                    {
                        float lq = _PxLev - 1;
                        float st = 255 / lq;
                        c = clamp(floor((c + dd * st) / st + 0.5), 0, lq) * st;
                    }
                    else if (_PxColor > 1.5 && _PxColor < 2.5)
                    {
                        float3 q = clamp(c + dd * 56, 0, 255);
                        float bd = 1e20; float3 best = 0;
                        [loop] for (int k = 0; k < (int)_PalN; k++)
                        {
                            float3 dv = q - _Pal[k].rgb;
                            float dist = dv.r * dv.r * 2.4 + dv.g * dv.g * 4.2 + dv.b * dv.b * 1.8;
                            if (dist < bd) { bd = dist; best = _Pal[k].rgb; }
                        }
                        c = best;
                    }
                    else if (_PxColor > 2.5)
                    {
                        float rq = _PxRampN - 1;
                        float lum = dot(c, float3(0.299, 0.587, 0.114)) / 255;
                        int k = (int)clamp(floor((lum + dd / rq) * rq + 0.5), 0, rq);
                        c = _Ramp[k].rgb;
                    }
                }
                return float4(round(c) / 255, a);
            }
            ENDCG
        }

        // 4: пиксель-арт — обводка в один пиксель сетки (4-связность), выход премультиплицирован
        Pass
        {
            Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float _PxOut;
            float _PxOuter;
            float3 _PxOutCol; // 0..1

            float In(float2 p) { return TexelAt(p).a > 127.0 / 255 ? 1 : 0; }

            float4 frag(v2f i) : SV_Target
            {
                float2 p = floor(i.cpos);
                float2 sz = _MainTex_TexelSize.zw;
                float4 c = TexelAt(p);
                if (_PxOut > 0)
                {
                    float me = In(p);
                    bool edge = (p.x > 0 && In(p - float2(1, 0)) != me) || (p.x < sz.x - 1 && In(p + float2(1, 0)) != me)
                             || (p.y > 0 && In(p - float2(0, 1)) != me) || (p.y < sz.y - 1 && In(p + float2(0, 1)) != me);
                    if (edge)
                    {
                        if (_PxOuter > 0.5 && me < 0.5) c = float4(_PxOutCol, round(255 * _PxOut) / 255);
                        else if (_PxOuter < 0.5 && me > 0.5) c.rgb = lerp(c.rgb, _PxOutCol, _PxOut);
                    }
                }
                return float4(c.rgb * c.a, c.a);
            }
            ENDCG
        }
    }
}
