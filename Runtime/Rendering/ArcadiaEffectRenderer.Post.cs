using System;
using System.Collections.Generic;
using UnityEngine;

namespace InteractiveSoftware.ArcadiaEffects
{
    // Постэффекты слоёв postfx и полноэкранные проходы холста.
    public sealed partial class ArcadiaEffectRenderer
    {
        private static readonly int CoordId = Shader.PropertyToID("_Coord");
        private static readonly int TexelId = Shader.PropertyToID("_MainTex_TexelSize");
        private static readonly int GridId = Shader.PropertyToID("_Grid");
        private static readonly int GridTexelId = Shader.PropertyToID("_Grid_TexelSize");

        private readonly Dictionary<Layer, PostP> _postCache = new Dictionary<Layer, PostP>();
        private readonly Dictionary<Layer, Texture2D> _gridsCpu = new Dictionary<Layer, Texture2D>();
        private float[] _gridData;

        private void DisposePost()
        {
            foreach (var g in _gridsCpu.Values) MeshPool.Destroy(g);
            _gridsCpu.Clear();
        }

        /// <summary>Источник полноэкранного прохода (+ его размер в текселях — им шейдер адресует пиксели).</summary>
        private void SetSrc(Texture t)
        {
            _pp.SetTexture(Materials.MainTex, t);
            _pp.SetVector(TexelId, new Vector4(1f / t.width, 1f / t.height, t.width, t.height));
        }

        private void RadialFade(RenderTexture lay, Layer layer, double tl)
        {
            double r = Math.Max(1, layer.FadeR.Eval(tl));
            double soft = Js.Clamp(layer.FadeSoft, 0, 1);
            double fx = layer.FadeX.Eval(tl), fy = layer.FadeY.Eval(tl);
            double inner = Math.Max(0, r * (1 - soft));
            double kk = Math.Max(0.2, _k);
            FlushBatch();
            _pp.Clear();
            _pp.SetVector(CoordId, new Vector4((float)_cw, (float)_ch, _scale, _scale));
            _pp.SetVector("_Fade", new Vector4((float)(_cx + fx), (float)(_cy + fy * _k), (float)inner, (float)r));
            _pp.SetFloat("_FadeKK", (float)kk);
            PostPass(lay, _cw, _ch, 0);
        }

        /// <summary>Гауссов блюр холста (CSS blur(r px)): при большом радиусе считается на уменьшенной копии.</summary>
        private RenderTexture BlurOf(RenderTexture src, double radiusComp)
        {
            double sigma = radiusComp * _scale;
            float maxSigma = Mathf.Max(1f, Profile.layerGlowMaxSigma);
            int f = 1;
            while (sigma / f > maxSigma && f < 16) f *= 2;
            RenderTexture cur = src;
            int w = src.width, h = src.height;
            for (int s = 1; s < f; s *= 2)
            {
                w = Math.Max(1, (w + 1) / 2);
                h = Math.Max(1, (h + 1) / 2);
                RenderTexture d = Temp(false, w, h, 1);
                BindClear(d, _cw, _ch);
                DrawFull(d, cur, DrawBlend.Copy, 1);
                cur = d;
            }
            RenderTexture a = Temp(false, w, h, 1);
            RenderTexture b = Temp(false, w, h, 1);
            float sg = (float)(sigma / f);
            FlushBatch();
            _pp.Clear();
            SetSrc(cur);
            _pp.SetVector(CoordId, new Vector4(w, h, 1, 1));
            _pp.SetVector("_BlurDir", new Vector4(1, 0, 0, 0));
            _pp.SetFloat("_Sigma", sg);
            PostPass(a, w, h, 1);
            _pp.Clear();
            SetSrc(a);
            _pp.SetVector(CoordId, new Vector4(w, h, 1, 1));
            _pp.SetVector("_BlurDir", new Vector4(0, 1, 0, 0));
            _pp.SetFloat("_Sigma", sg);
            PostPass(b, w, h, 1);
            return b;
        }

        // =====================================================================
        // ПАРАМЕТРЫ (без аллокаций в кадре: один объект на слой)
        // =====================================================================

        private sealed class PostP
        {
            public PostEffect Effect;
            public Layer Layer;
            public double Mix;
            // mblur
            public bool Radial, Half;
            public double Len, Angle, Spin, X, Y;
            public int Passes;
            // displace
            public double Amount, Scale, Aspect, Oct, Z, Scroll, Ramp, Y0, Soft, Seed;
            public bool Swirl;
            // pixelate
            public PostFxSettings Fx;
            public double Thr;
        }

        /// <summary>Параметры постэффекта в момент t; null — делать нечего (байпас).</summary>
        private PostP PostParams(Layer layer, double t)
        {
            PostFxSettings fx = layer.Fx;
            if (fx == null || fx.Effect == PostEffect.None) return null;
            if (fx.Effect == PostEffect.Displace && !Profile.displace) return null;
            if (fx.Effect == PostEffect.MotionBlur && !Profile.motionBlur) return null;
            double tl = t - layer.Start;
            double win = Math.Max(0.0001, layer.End - layer.Start);
            if (tl < 0 || tl > win) return null;
            double mix = Js.Clamp(layer.Opacity.Eval(tl), 0, 1);
            if (mix <= 0.004) return null;
            if (!_postCache.TryGetValue(layer, out PostP p)) _postCache[layer] = p = new PostP();
            p.Layer = layer;
            p.Mix = mix;
            p.Effect = fx.Effect;
            switch (fx.Effect)
            {
                case PostEffect.Displace:
                {
                    double amount = Math.Max(0, fx.Amount.Eval(tl)) * mix;
                    if (amount < 0.4) return null;
                    int oct = Js.ToInt32(fx.Oct);
                    p.Amount = amount;
                    p.Scale = Math.Max(4, fx.Scale != 0 && !double.IsNaN(fx.Scale) ? fx.Scale : 60);
                    p.Aspect = Js.Clamp(fx.Aspect != 0 && !double.IsNaN(fx.Aspect) ? fx.Aspect : 1, 0.2, 8);
                    p.Oct = Js.Clamp(oct != 0 ? oct : 1, 1, 4);
                    p.Swirl = fx.Swirl;
                    p.Z = t * fx.Evo;
                    p.Scroll = -fx.Rise * t;
                    p.Ramp = Js.Clamp(fx.Ramp, 0, 1);
                    p.Y0 = fx.Y0.Eval(tl);
                    p.Soft = Math.Max(1, fx.Soft != 0 && !double.IsNaN(fx.Soft) ? fx.Soft : 120);
                    p.Seed = (double)Js.ToInt32(fx.Seed) * 3571;
                    return p;
                }
                case PostEffect.Pixelate:
                    p.Fx = fx;
                    p.Thr = Js.Clamp(fx.PxThr.Eval(tl), 0, 1);
                    return p;
                default:
                {
                    double len = Math.Max(0, fx.Length.Eval(tl));
                    double spin = fx.Radial ? fx.Spin.Eval(tl) : 0;
                    if (len < 0.5 && Math.Abs(spin) < 0.05) return null;
                    int passes = (int)Js.Clamp(Js.Round(Math.Log(Math.Max(2, Js.ToInt32(fx.Samples))) / Math.Log(2)), 1, 6);
                    int maxPasses = Mathf.Max(1, Mathf.FloorToInt(Mathf.Log(Mathf.Max(2, Profile.motionBlurMaxSamples), 2)));
                    p.Radial = fx.Radial;
                    p.Len = len;
                    p.Angle = fx.Angle.Eval(tl) * Js.DEG;
                    p.Spin = spin * Js.DEG;
                    p.X = fx.X.Eval(tl);
                    p.Y = fx.Y.Eval(tl);
                    p.Passes = Math.Min(passes, maxPasses);
                    p.Half = fx.Half;
                    return p;
                }
            }
        }

        private void ApplyPost(RenderTexture x, PostP p)
        {
            if (p == null) return;
            using (MarkPost.Auto())
            {
                switch (p.Effect)
                {
                    case PostEffect.Displace: ApplyDisplace(x, p); break;
                    case PostEffect.Pixelate: ApplyPixelate(x, p); break;
                    default: ApplyMotionBlur(x, p); break;
                }
            }
        }

        // =====================================================================
        // СМАЗ
        // =====================================================================

        private static Affine TapMat(bool radial, double pcx, double pcy, double dx, double dy, double rot, double sc)
        {
            if (radial)
                return Affine.Translate(pcx, pcy).Mul(Affine.Rotate(rot)).Mul(Affine.Scale(sc, sc)).Mul(Affine.Translate(-pcx, -pcy));
            return Affine.Translate(dx, dy);
        }

        private void ApplyMotionBlur(RenderTexture x, PostP p)
        {
            int n = 1 << p.Passes;
            double q = p.Half ? 0.5 : 1;
            double bw = Math.Max(1, Js.Round(_cw * q)), bh = Math.Max(1, Js.Round(_ch * q));
            double pcx = (_cx + p.X) * q, pcy = (_cy + p.Y * _k) * q;
            double half = (n - 1) / 2.0;

            double dx = 0, dy = 0, step = 1, rot = 0;
            if (p.Radial)
            {
                double R = Math.Max(1, Math.Sqrt(Sq(Math.Max(pcx, bw - pcx)) + Sq(Math.Max(pcy, bh - pcy))));
                double u = p.Len * q / R;
                step = Math.Pow(Math.Pow((u + Math.Sqrt(u * u + 4)) / 2, 2), 1.0 / (n - 1));
                rot = p.Spin / (n - 1);
            }
            else
            {
                dx = Math.Cos(p.Angle) * p.Len * q / (n - 1);
                dy = Math.Sin(p.Angle) * p.Len * q / (n - 1);
            }

            RenderTexture orig = CopyOf(x);
            int rw = Math.Max(1, Mathf.RoundToInt((float)(bw * _scale))), rh = Math.Max(1, Mathf.RoundToInt((float)(bh * _scale)));
            RenderTexture a = Temp(false, rw, rh, 1), b = Temp(false, rw, rh, 1);
            var full = new Vector4(0, 1, 1, 0);
            var white = new Vector4(1, 1, 1, 1);

            BindClear(a, bw, bh);
            Affine m0 = TapMat(p.Radial, pcx, pcy, -dx * half, -dy * half, -rot * half, Math.Pow(step, -half)).Mul(Affine.Scale(q, q));
            AddQuad(orig, DrawBlend.Normal, m0, 0, 0, (float)_cw, (float)_ch, full, white, false);

            var halfA = new Vector4(1, 1, 1, 0.5f);
            for (int j = 0; j < p.Passes; j++)
            {
                int mm = 1 << j;
                BindClear(b, bw, bh);
                AddQuad(a, DrawBlend.Add, Affine.Identity, 0, 0, (float)bw, (float)bh, full, halfA, false);
                AddQuad(a, DrawBlend.Add, TapMat(p.Radial, pcx, pcy, dx * mm, dy * mm, rot * mm, Math.Pow(step, mm)),
                    0, 0, (float)bw, (float)bh, full, halfA, false);
                RenderTexture tmp = a; a = b; b = tmp;
            }

            BindClear(x, _cw, _ch);
            AddQuad(a, DrawBlend.Normal, Affine.Identity, 0, 0, (float)_cw, (float)_ch, full, new Vector4(1, 1, 1, (float)p.Mix), false);
            if (p.Mix < 0.999) DrawFull(x, orig, DrawBlend.Add, 1 - p.Mix);
        }

        private static double Sq(double v) => v * v;

        // =====================================================================
        // ИСКАЖЕНИЕ (displace): сетка смещений на GPU, CPU — запасной путь
        // =====================================================================

        private static RenderTextureFormat? _gridFormat;

        private static RenderTextureFormat GridFormat
        {
            get
            {
                if (_gridFormat.HasValue) return _gridFormat.Value;
                if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf)) _gridFormat = RenderTextureFormat.RGHalf;
                else if (SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)) _gridFormat = RenderTextureFormat.ARGBHalf;
                else _gridFormat = RenderTextureFormat.Default; // нет half-RT — сетка на CPU
                return _gridFormat.Value;
            }
        }

        private void ApplyDisplace(RenderTexture x, PostP p)
        {
            int cw = (int)Math.Round(_cw), ch = (int)Math.Round(_ch);
            int gsStep = (int)Math.Max(2, Math.Min(12, Js.Round(p.Scale / 16))) * Mathf.Clamp(Profile.displaceGridMultiplier, 1, 4);
            int gw = (int)Math.Ceiling((double)cw / gsStep) + 2, gh = (int)Math.Ceiling((double)ch / gsStep) + 2;

            Texture grid;
            bool gridRt = GridFormat != RenderTextureFormat.Default && SystemInfo.graphicsShaderLevel >= 35;
            if (gridRt)
            {
                RenderTexture g = Temp(false, gw, gh, 1, FilterMode.Point, GridFormat);
                FlushBatch();
                _pp.Clear();
                _pp.SetVector(CoordId, new Vector4(gw, gh, 1, 1));
                _pp.SetVector("_DA", new Vector4(gsStep, (float)p.Amount, (float)(1 / p.Scale), (float)(1 / p.Scale / p.Aspect)));
                _pp.SetVector("_DB", new Vector4((float)p.Ramp, (float)(_ch / 2), (float)p.Y0, (float)p.Soft));
                _pp.SetVector("_DC", new Vector4((float)p.Z, (float)p.Oct, (float)p.Seed, (float)p.Scroll));
                _pp.SetFloat("_DSwirl", p.Swirl ? 1 : 0);
                PostPass(g, gw, gh, 5);
                grid = g;
            }
            else
            {
                grid = CpuGrid(p, gw, gh, gsStep);
            }

            RenderTexture src = CopyOf(x);
            BindClear(x, _cw, _ch);
            _pp.Clear();
            SetSrc(src);
            _pp.SetTexture(GridId, grid);
            _pp.SetVector(GridTexelId, new Vector4(1f / grid.width, 1f / grid.height, grid.width, grid.height));
            _pp.SetFloat("_GridFlip", gridRt ? 1 : 0);
            _pp.SetFloat("_GridStep", gsStep);
            _pp.SetVector(CoordId, new Vector4((float)_cw, (float)_ch, _scale, _scale));
            PostPass(x, _cw, _ch, 2);
        }

        /// <summary>Сетка смещений на CPU (как в редакторе) — для устройств без half-RenderTexture.</summary>
        private Texture2D CpuGrid(PostP p, int gw, int gh, int gsStep)
        {
            int need = gw * gh * 2;
            if (_gridData == null || _gridData.Length < need) _gridData = new float[need];
            float[] data = _gridData;
            double invS = 1 / p.Scale, invSY = invS / p.Aspect;
            double cy2 = _ch / 2;
            for (int j = 0; j < gh; j++)
            {
                double py = j * gsStep;
                double m = p.Ramp > 0 ? 1 - p.Ramp * Js.Clamp((py - cy2 - p.Y0) / p.Soft, 0, 1) : 1;
                double a = p.Amount * m;
                double ny = (py + p.Scroll) * invSY;
                for (int i = 0; i < gw; i++)
                {
                    double nx = i * gsStep * invS;
                    int g = (j * gw + i) * 2;
                    if (p.Swirl)
                    {
                        Js.TurbCurl3(nx, ny, p.Z, p.Oct, p.Seed, out double vx, out double vy);
                        data[g] = (float)(a * vx);
                        data[g + 1] = (float)(a * vy);
                    }
                    else
                    {
                        data[g] = (float)(a * (Js.Turb3(nx, ny, p.Z, p.Oct, p.Seed) * 2 - 1));
                        data[g + 1] = (float)(a * (Js.Turb3(nx, ny, p.Z, p.Oct, p.Seed + 91193) * 2 - 1));
                    }
                }
            }
            bool fl = SystemInfo.SupportsTextureFormat(TextureFormat.RGFloat);
            if (!_gridsCpu.TryGetValue(p.Layer, out Texture2D tex) || tex == null || tex.width != gw || tex.height != gh)
            {
                if (tex != null) MeshPool.Destroy(tex);
                tex = new Texture2D(gw, gh, fl ? TextureFormat.RGFloat : TextureFormat.RGHalf, false, true)
                {
                    name = "ArcadiaDisplaceGrid", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _gridsCpu[p.Layer] = tex;
            }
            if (fl) tex.SetPixelData(data, 0);
            else
            {
                var h = new ushort[need];
                for (int i = 0; i < need; i++) h[i] = Mathf.FloatToHalf(data[i]);
                tex.SetPixelData(h, 0);
            }
            tex.Apply(false, false);
            return tex;
        }

        // =====================================================================
        // ПИКСЕЛЬ-АРТ
        // =====================================================================

        private static float[] _bayer2, _bayer4, _bayer8;

        private static float[] Bayer(int n)
        {
            ref float[] cache = ref _bayer2;
            if (n == 4) cache = ref _bayer4;
            else if (n == 8) cache = ref _bayer8;
            if (cache != null) return cache;
            int[,] m = { { 0 } };
            int sz = 1;
            while (sz < n)
            {
                var r = new int[sz * 2, sz * 2];
                for (int y = 0; y < sz; y++)
                for (int x = 0; x < sz; x++)
                {
                    int v = m[y, x] * 4;
                    r[y, x] = v; r[y, x + sz] = v + 2;
                    r[y + sz, x] = v + 3; r[y + sz, x + sz] = v + 1;
                }
                m = r;
                sz *= 2;
            }
            var o = new float[64];
            float d = n * n;
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                o[y * n + x] = m[y, x] / d - 0.5f + 0.5f / d;
            cache = o;
            return o;
        }

        private readonly Vector4[] _pal = new Vector4[64];
        private readonly Vector4[] _ramp = new Vector4[32];
        private static readonly float[] NoBayer = new float[64];

        private void ApplyPixelate(RenderTexture x, PostP pp)
        {
            PostFxSettings fx = pp.Fx;
            PixelGrid g = Data.Document.PixelGridOf(fx);
            double gain = Math.Max(0, fx.PxGain);
            string alphaMode = fx.PxAlpha ?? "cut";
            string colorMode = fx.PxColor ?? "none";
            double sat = Js.Clamp(fx.PxSat, 0, 3), con = Js.Clamp(fx.PxCon, 0.2, 3);
            double outAmt = Js.Clamp(fx.PxOut, 0, 1);
            if (g.Bx <= 1.0001 && g.By <= 1.0001 && alphaMode == "soft" && colorMode == "none" && outAmt <= 0 && gain == 1 && sat == 1 && con == 1)
                return;

            int aLev = Js.ToInt32(fx.PxALev), lev = Js.ToInt32(fx.PxLev), rampN = Js.ToInt32(fx.PxRampN), dith = Js.ToInt32(fx.PxDither);
            aLev = (int)Js.Clamp(aLev != 0 ? aLev : 4, 2, 16);
            lev = (int)Js.Clamp(lev != 0 ? lev : 6, 2, 32);
            rampN = (int)Js.Clamp(rampN != 0 ? rampN : 6, 2, 32);
            dith = dith == 2 || dith == 4 || dith == 8 ? dith : 0;

            RenderTexture src = CopyOf(x);
            RenderTexture a = Temp(false, g.Gw, g.Gh, 1, FilterMode.Point);
            RenderTexture b = Temp(false, g.Gw, g.Gh, 1, FilterMode.Point);

            FlushBatch();
            _pp.Clear();
            SetSrc(src);
            _pp.SetVector(CoordId, new Vector4(g.Gw, g.Gh, 1, 1));
            _pp.SetVector("_PxGrid", new Vector4((float)g.Bx, (float)g.By, (float)g.Ox, (float)g.Oy));
            _pp.SetFloat("_PxScale", _scale);
            _pp.SetFloat("_PxMaxBlock", Mathf.Clamp(Profile.pixelBlockMax, 4, 96));
            string sample = fx.PxSample ?? "peak";
            _pp.SetFloat("_PxSample", sample == "box" ? 1 : sample == "point" ? 2 : 0);
            _pp.SetFloat("_PxGain", (float)gain);
            _pp.SetFloat("_PxAlpha", alphaMode == "steps" ? 1 : alphaMode == "soft" ? 2 : 0);
            _pp.SetFloat("_PxThr", (float)pp.Thr);
            _pp.SetFloat("_PxALev", aLev);
            _pp.SetFloat("_PxColor", colorMode == "levels" ? 1 : colorMode == "palette" ? 2 : colorMode == "ramp" ? 3 : 0);
            _pp.SetFloat("_PxLev", lev);
            _pp.SetFloat("_PxRampN", rampN);
            _pp.SetFloat("_PxSat", (float)sat);
            _pp.SetFloat("_PxCon", (float)con);
            _pp.SetFloat("_PxDither", dith);
            _pp.SetFloat("_PxDitherAmt", (float)Js.Clamp(fx.PxDitherAmt, 0, 2));
            _pp.SetFloatArray("_Bayer", dith > 0 ? Bayer(dith) : NoBayer);
            string[] pal = PixelPalettes.Get(fx.PxPal);
            int pn = Math.Min(pal.Length, 64);
            for (int i = 0; i < 64; i++)
            {
                if (i < pn)
                {
                    ModelUtil.HexToRgb(pal[i], out double r, out double gg, out double bb);
                    _pal[i] = new Vector4((float)r, (float)gg, (float)bb, 0);
                }
                else _pal[i] = Vector4.zero;
            }
            _pp.SetFloat("_PalN", pn);
            _pp.SetVectorArray("_Pal", _pal);
            for (int i = 0; i < 32; i++)
            {
                if (i < rampN)
                {
                    fx.PxRamp.Eval(rampN == 1 ? 0 : (double)i / (rampN - 1), out double r, out double gg, out double bb);
                    _ramp[i] = new Vector4((float)Math.Floor(r), (float)Math.Floor(gg), (float)Math.Floor(bb), 0);
                }
                else _ramp[i] = Vector4.zero;
            }
            _pp.SetVectorArray("_Ramp", _ramp);
            BindClear(a, g.Gw, g.Gh);
            PostPass(a, g.Gw, g.Gh, 3);

            _pp.Clear();
            SetSrc(a);
            _pp.SetVector(CoordId, new Vector4(g.Gw, g.Gh, 1, 1));
            _pp.SetFloat("_PxOut", (float)outAmt);
            _pp.SetFloat("_PxOuter", fx.PxOutMode == "inner" ? 0 : 1);
            double[] oc = fx.PxOutCol ?? new double[] { 0, 0, 0 };
            _pp.SetVector("_PxOutCol", new Vector4((float)(oc[0] / 255), (float)(oc[1] / 255), (float)(oc[2] / 255), 0));
            BindClear(b, g.Gw, g.Gh);
            PostPass(b, g.Gw, g.Gh, 4);

            BindClear(x, _cw, _ch);
            var m = new Affine { A = g.Bx, D = g.By, E = g.Ox, F = g.Oy };
            AddQuad(b, DrawBlend.Normal, m, 0, 0, g.Gw, g.Gh, new Vector4(0, 1, 1, 0), new Vector4(1, 1, 1, (float)pp.Mix), false);
            if (pp.Mix < 0.999) DrawFull(x, src, DrawBlend.Add, 1 - pp.Mix);
        }
    }
}
