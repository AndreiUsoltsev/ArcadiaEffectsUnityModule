using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Рендер композиции эффекта в момент t в <see cref="RenderTexture"/> — порт js/core/engine.js.
    /// Порядок слоёв, режимы наложения (Canvas2D, премультиплицированная альфа), свечение,
    /// затухание, трейлы, группы корректоров и постэффекты повторяют редактор.
    /// У каждого экземпляра свои симуляции (как у каждого Engine в редакторе).
    /// </summary>
    public sealed class ArcadiaEffectRenderer : IDisposable
    {
        public ArcadiaEffectData Data { get; }
        /// <summary>Готовая композиция (премультиплицированная альфа, значения в гамме).</summary>
        public RenderTexture Output { get; private set; }
        /// <summary>Число живых частиц на последнем кадре.</summary>
        public int ParticleCount { get; private set; }

        private readonly float _scale;
        private readonly int _msaa;
        private readonly int _rtW, _rtH;
        private readonly CommandBuffer _cmd = new CommandBuffer { name = "ArcadiaEffect" };
        private readonly MeshPool _pool = new MeshPool();
        private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();   // пакеты
        private readonly MaterialPropertyBlock _pp = new MaterialPropertyBlock();    // полноэкранные проходы
        private readonly Dictionary<string, Simulation> _sims = new Dictionary<string, Simulation>();
        private readonly Dictionary<object, ShapeAtlas.Sheet> _sheets = new Dictionary<object, ShapeAtlas.Sheet>();
        private readonly Dictionary<Layer, Texture2D> _grids = new Dictionary<Layer, Texture2D>();
        private readonly List<RenderTexture> _temps = new List<RenderTexture>();
        private readonly List<Layer> _vis = new List<Layer>();
        private Mesh _quad;
        private bool _disposed;

        private double _cw, _ch, _cx, _cy, _k;

        /// <param name="data">Загруженный эффект.</param>
        /// <param name="resolutionScale">Масштаб RenderTexture относительно размера композиции.</param>
        /// <param name="msaa">Сглаживание (1, 2, 4, 8) — нужно лентам трейлов и молниям.</param>
        public ArcadiaEffectRenderer(ArcadiaEffectData data, float resolutionScale = 1f, int msaa = 4)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            _scale = Mathf.Clamp(resolutionScale, 0.1f, 4f);
            _msaa = msaa <= 1 ? 1 : (msaa <= 2 ? 2 : (msaa <= 4 ? 4 : 8));
            var doc = data.Document;
            _cw = doc.CompW; _ch = doc.CompH;
            _rtW = Mathf.Max(1, Mathf.RoundToInt((float)(_cw * _scale)));
            _rtH = Mathf.Max(1, Mathf.RoundToInt((float)(_ch * _scale)));
            if (_msaa > 1)
            {
                int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(CanvasDesc(true));
                _msaa = Mathf.Max(1, Mathf.Min(_msaa, supported));
            }
            Output = new RenderTexture(CanvasDesc(true)) { name = "Arcadia_" + doc.Name, hideFlags = HideFlags.DontSave, filterMode = FilterMode.Bilinear };
            Output.Create();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseTemps();
            _pool.Dispose();
            foreach (var g in _grids.Values) MeshPool.Destroy(g);
            _grids.Clear();
            MeshPool.Destroy(_quad);
            _cmd.Release();
            if (Output != null)
            {
                Output.Release();
                MeshPool.Destroy(Output);
                Output = null;
            }
        }

        /// <summary>Сбросить симуляции (следующий кадр пересчитает всё с нуля).</summary>
        public void ResetSimulations() => _sims.Clear();

        /// <summary>Очистить выход в прозрачный.</summary>
        public void Clear()
        {
            if (Output == null) return;
            _cmd.Clear();
            _cmd.SetRenderTarget(Output);
            _cmd.ClearRenderTarget(true, true, Color.clear);
            Graphics.ExecuteCommandBuffer(_cmd);
            _cmd.Clear();
        }

        private RenderTextureDescriptor CanvasDesc(bool stencil, int w = 0, int h = 0, int msaa = -1)
        {
            var d = new RenderTextureDescriptor(w > 0 ? w : _rtW, h > 0 ? h : _rtH, RenderTextureFormat.ARGB32, stencil ? 24 : 0)
            {
                sRGB = false,
                msaaSamples = msaa < 0 ? _msaa : msaa,
                useMipMap = false,
                autoGenerateMips = false
            };
            return d;
        }

        private RenderTexture Temp(bool stencil, int w = 0, int h = 0, int msaa = -1, FilterMode filter = FilterMode.Bilinear)
        {
            var rt = RenderTexture.GetTemporary(CanvasDesc(stencil, w, h, msaa));
            rt.filterMode = filter;
            rt.wrapMode = TextureWrapMode.Clamp;
            _temps.Add(rt);
            return rt;
        }

        private void ReleaseTemps()
        {
            foreach (var rt in _temps) RenderTexture.ReleaseTemporary(rt);
            _temps.Clear();
        }

        // =====================================================================
        // КАДР
        // =====================================================================

        /// <summary>Отрисовать композицию в момент <paramref name="t"/> (секунды композиции).</summary>
        public int Render(double t)
        {
            if (_disposed || Output == null) return 0;
            if (!Materials.Ready) return 0;
            if (!Output.IsCreated()) Output.Create();
            EffectDocument doc = Data.Document;
            _cw = doc.CompW; _ch = doc.CompH;
            _cx = _cw / 2; _cy = _ch / 2;
            _k = doc.CompK;

            _cmd.Clear();
            _pool.Reset();
            _curTarget = null;
            int count = 0;

            BindClear(Output, _cw, _ch);

            _vis.Clear();
            bool anySolo = false;
            foreach (var l in doc.Layers) if (l.Solo && l.On) { anySolo = true; break; }
            foreach (var l in doc.Layers) if (l.On && (!anySolo || l.Solo)) _vis.Add(l);
            List<Layer> layers = _vis;

            PostGroups(layers, t, out Dictionary<int, int> claimed, out Dictionary<int, List<int>> byFx);

            for (int i = layers.Count - 1; i >= 0; i--)
            {
                Layer layer = layers[i];
                if (claimed != null && claimed.ContainsKey(i)) continue;
                if (layer.Type == LayerType.Force) continue;
                if (layer.Type == LayerType.PostFx)
                {
                    if (byFx != null && byFx.TryGetValue(i, out List<int> grp))
                    {
                        RenderTexture g = Temp(true);
                        BindClear(g, _cw, _ch);
                        for (int q = grp.Count - 1; q >= 0; q--) count += DrawLayer(layers[grp[q]], t, g);
                        ApplyPost(g, PostParams(layer, t));
                        DrawFull(Output, g, ToDraw(layer.Blend), 1);
                        continue;
                    }
                    if (layer.HasTargets) continue;
                    PostP pp = PostParams(layer, t);
                    if (pp != null) ApplyPost(Output, pp);
                    continue;
                }
                count += DrawLayer(layer, t, Output);
            }
            FlushBatch();
            Graphics.ExecuteCommandBuffer(_cmd);
            _cmd.Clear();
            ReleaseTemps();
            ParticleCount = count;
            return count;
        }

        private static DrawBlend ToDraw(LayerBlend b)
        {
            switch (b)
            {
                case LayerBlend.Add: return DrawBlend.Add;
                case LayerBlend.Screen: return DrawBlend.Screen;
                case LayerBlend.Multiply: return DrawBlend.Multiply;
                default: return DrawBlend.Normal;
            }
        }

        /// <summary>Группы адресованных корректоров (<c>postGroups</c>).</summary>
        private void PostGroups(List<Layer> layers, double t, out Dictionary<int, int> claimed, out Dictionary<int, List<int>> byFx)
        {
            claimed = null;
            byFx = null;
            bool any = false;
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].Type == LayerType.PostFx && layers[i].HasTargets) { any = true; break; }
            if (!any) return;
            var cl = new Dictionary<int, int>();
            var bf = new Dictionary<int, List<int>>();
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                Layer fl = layers[i];
                if (fl.Type != LayerType.PostFx || !fl.HasTargets || PostParams(fl, t) == null) continue;
                var set = new HashSet<string>(fl.Targets);
                var g = new List<int>();
                for (int j = i + 1; j < layers.Count; j++)
                {
                    Layer c = layers[j];
                    if (c.Type == LayerType.PostFx || c.Type == LayerType.Force) continue;
                    if (cl.ContainsKey(j) || !set.Contains(c.Id)) continue;
                    cl[j] = i;
                    g.Add(j);
                }
                if (g.Count > 0) bf[i] = g;
            }
            if (bf.Count == 0) return;
            claimed = cl;
            byFx = bf;
        }

        /// <summary>Один слой композиции (<c>drawLayer</c>). Возвращает число частиц.</summary>
        private int DrawLayer(Layer layer, double t, RenderTexture target)
        {
            double tl = t - layer.Start;
            double gAmt = Js.Clamp(layer.GlowL.Eval(tl), 0, 3);
            bool fadeOn = layer.FadeOn;
            int count = 0;

            if (gAmt <= 0.01 && !fadeOn)
            {
                Bind(target, _cw, _ch);
                if (layer.Type == LayerType.Emitter) count += DrawEmitter(layer, t);
                else DrawSprite(layer, t);
                return count;
            }

            RenderTexture lay = Temp(true);
            BindClear(lay, _cw, _ch);
            if (layer.Type == LayerType.Emitter) count += DrawEmitter(layer, t);
            else DrawSprite(layer, t);

            if (fadeOn) RadialFade(lay, layer, tl);

            DrawFull(target, lay, ToDraw(layer.Blend), 1);

            double r = layer.GlowLR != 0 && !double.IsNaN(layer.GlowLR) ? layer.GlowLR : 24;
            RenderTexture blur = BlurOf(lay, Math.Max(1, r));
            for (double rem = gAmt; rem > 0.01; rem -= 1)
                DrawFull(target, blur, DrawBlend.Add, Math.Min(1, rem));
            return count;
        }

        // =====================================================================
        // ЭМИТТЕР
        // =====================================================================

        private Simulation SimFor(Layer layer)
        {
            if (!_sims.TryGetValue(layer.Id, out var sim))
            {
                sim = new Simulation(layer, Data.Document.ForcesFor(layer.Id));
                _sims[layer.Id] = sim;
            }
            return sim;
        }

        private int DrawEmitter(Layer layer, double t)
        {
            double tl = t - layer.Start;
            if (tl < 0) return 0;
            Simulation sim = SimFor(layer);
            double ext = sim.Ensure(tl);
            List<Particle> parts = sim.Parts;
            if (parts.Count == 0) return 0;

            double layerAlpha = Js.Clamp(layer.Opacity.Eval(tl), 0, 1);
            if (layerAlpha <= 0) return parts.Count;

            DrawBlend blend = ToDraw(layer.Blend);
            SubEmitterSettings sub = layer.Em.Sub;
            if (!sub.On)
            {
                DrawPass(parts, layer.Pt, -1, layerAlpha, ext, blend);
            }
            else if (sub.Under)
            {
                DrawPass(parts, sub.Pt, 1, layerAlpha, ext, blend);
                DrawPass(parts, layer.Pt, 0, layerAlpha, ext, blend);
            }
            else
            {
                DrawPass(parts, layer.Pt, 0, layerAlpha, ext, blend);
                DrawPass(parts, sub.Pt, 1, layerAlpha, ext, blend);
            }
            return parts.Count;
        }

        private ShapeAtlas.Sheet SheetFor(object owner, SpriteRef r, double glowBlur)
        {
            if (!_sheets.TryGetValue(owner, out var s) || s.Texture == null)
            {
                s = ShapeAtlas.GetSheet(r, glowBlur);
                _sheets[owner] = s;
            }
            return s;
        }

        private void DrawPass(List<Particle> parts, ParticleSettings pt, int tierSel, double layerAlpha, double ext, DrawBlend blend)
        {
            double k = _k;
            double squashK = 1 - (1 - k) * Js.Clamp(pt.Squash, 0, 1);
            bool isTex = pt.Sprite.IsTexture;
            EffectTexture tex = isTex ? Data.GetTexture(pt.Sprite.TexId) : null;
            bool useSheet = tex != null && tex.Info.HasSheet;
            bool glowOn = pt.Glow > 0.01;
            int fixVar = (!isTex && pt.Sprite.Var.HasValue && pt.Sprite.Var.Value >= 0) ? (int)pt.Sprite.Var.Value : -1;
            ShapeAtlas.Sheet sheet = isTex ? null : SheetFor(pt, pt.Sprite, pt.GlowBlur);
            double glowSize = pt.GlowSize != 0 && !double.IsNaN(pt.GlowSize) ? pt.GlowSize : 1.5;

            for (int i = 0; i < parts.Count; i++)
            {
                Particle p = parts[i];
                if (tierSel >= 0 && (p.Tier > 0 ? 1 : 0) != tierSel) continue;
                double age = p.Age + ext;
                double lf = Js.Clamp(age / p.Life, 0, 1);
                double alpha = layerAlpha * Js.Clamp(pt.OpacityOL.Eval(lf), 0, 1);
                if (alpha <= 0.004) continue;
                double size = p.Size0 * Math.Max(0, pt.SizeOL.Eval(lf));
                if (size < 0.5) continue;

                if (pt.Trail)
                {
                    DrawTrail(p, pt, lf, alpha, size, ext, blend);
                    continue;
                }

                double wx = p.X + p.Vx * ext, wy = p.Y + p.Vy * ext;
                double px = _cx + wx, py = _cy + wy * k;

                double ang = p.Rot;
                double stretchX = 1;
                if (pt.AlignVel || pt.Stretch > 0)
                {
                    double svx = p.Vx, svy = p.Vy * k;
                    double spd = Math.Sqrt(svx * svx + svy * svy);
                    if (spd > 1)
                    {
                        double velAng = Math.Atan2(svy, svx);
                        if (pt.AlignVel) ang = velAng;
                        if (pt.Stretch > 0) { ang = velAng; stretchX = 1 + Math.Min(3.5, pt.Stretch * spd / 600); }
                    }
                }

                pt.Grad.HexAt(lf, out byte hr, out byte hg, out byte hb);
                bool white = hr == 255 && hg == 255 && hb == 255;
                Texture img;
                Vector4 uv;
                float iw, ih;
                bool tint;
                if (isTex)
                {
                    if (tex == null || tex.Texture == null) continue;
                    img = tex.Texture;
                    if (useSheet)
                    {
                        int total = Math.Max(1, tex.Info.SheetCols * tex.Info.SheetRows);
                        int idx = pt.Fps > 0 ? (int)(Math.Floor(age * pt.Fps) % total) : Math.Min(total - 1, (int)Math.Floor(lf * total));
                        uv = SheetUv(tex, idx, out iw, out ih);
                        tint = false;
                    }
                    else if (pt.TintTex && !white)
                    {
                        uv = new Vector4(0, 1, 1, 0);
                        iw = Math.Min(tex.Width, 256);
                        ih = Math.Min(tex.Height, 256);
                        tint = true;
                    }
                    else
                    {
                        uv = new Vector4(0, 1, 1, 0);
                        iw = tex.Width;
                        ih = tex.Height;
                        tint = false;
                    }
                }
                else
                {
                    SpriteImage si = sheet.Image(fixVar >= 0 ? fixVar : p.Vi);
                    img = si.Texture;
                    uv = si.Uv;
                    iw = si.Width;
                    ih = si.Height;
                    tint = true;
                }

                double scale = size / Math.Max(iw, ih);
                Affine m = Affine.Particle(px, py, ang, scale * stretchX, scale, squashK);
                Vector4 col = new Vector4(hr / 255f, hg / 255f, hb / 255f, (float)alpha);
                AddQuad(img, blend, m, -iw / 2, -ih / 2, iw / 2, ih / 2, uv, col, tint);

                if (glowOn && !isTex)
                {
                    SpriteImage si = sheet.Image(fixVar >= 0 ? fixVar : p.Vi);
                    const float gw = 192;
                    double gs = size * glowSize / (gw * 0.5);
                    Affine gm = Affine.Particle(px, py, ang, gs * stretchX, gs, squashK);
                    for (double rem = pt.Glow; rem > 0.01; rem -= 1)
                    {
                        col.w = (float)(alpha * Math.Min(1, rem));
                        AddQuad(img, DrawBlend.Add, gm, -gw / 2, -gw / 2, gw / 2, gw / 2, si.GlowUv, col, true);
                    }
                }
            }
        }

        private static Vector4 SheetUv(EffectTexture tex, int idx, out float fw, out float fh)
        {
            int cols = Math.Max(1, tex.Info.SheetCols), rows = Math.Max(1, tex.Info.SheetRows);
            int total = cols * rows;
            int i = Mathf.Clamp(idx, 0, total - 1);
            float w = tex.Width, h = tex.Height;
            fw = w / cols;
            fh = h / rows;
            float sx = (i % cols) * fw, sy = (i / cols) * fh;
            return new Vector4(sx / w, 1 - sy / h, (sx + fw) / w, 1 - (sy + fh) / h);
        }

        // ---------- трейлы ----------

        private readonly List<Vector2> _poly = new List<Vector2>();
        private double[] _rx = new double[64], _ry = new double[64], _rnx = new double[64], _rny = new double[64], _rhw = new double[64];
        private int _rn;

        private void DrawTrail(Particle p, ParticleSettings pt, double lf, double alpha, double width, double ext, DrawBlend blend)
        {
            double? cv = pt.SizeOT.ConstValue();
            if (cv.HasValue)
            {
                width *= cv.Value;
                if (width < 0.5) return;
                DrawTrailUniform(p, pt, lf, alpha, width, ext, blend);
                return;
            }
            if (RibbonBuild(p, ext) < 2)
            {
                DrawTrailUniform(p, pt, lf, alpha, width, ext, blend);
                return;
            }
            RibbonShape(pt.SizeOT, width);

            pt.Grad.HexAt(lf, out byte hr, out byte hg, out byte hb);
            var col = new Vector3(hr / 255f, hg / 255f, hb / 255f);
            double glowSize = pt.GlowSize != 0 && !double.IsNaN(pt.GlowSize) ? pt.GlowSize : 1.5;
            if (pt.Glow > 0.01)
            {
                RibbonPath(1 + glowSize, false);
                for (double rem = pt.Glow; rem > 0.01; rem -= 1)
                    FillPolygon(_poly, col, alpha * 0.5 * Math.Min(1, rem), DrawBlend.Add);
            }
            RibbonPath(1, false);
            FillPolygon(_poly, col, alpha, blend);
            if (pt.TrailCore > 0.01)
            {
                RibbonPath(0.35, true);
                FillPolygon(_poly, Vector3.one, alpha * Js.Clamp(pt.TrailCore, 0, 1), blend);
            }
        }

        private void DrawTrailUniform(Particle p, ParticleSettings pt, double lf, double alpha, double width, double ext, DrawBlend blend)
        {
            _poly.Clear();
            double k = _k;
            List<double> hist = p.Hist;
            if (hist != null && hist.Count >= 2)
            {
                for (int j = 0; j < hist.Count; j += 2) _poly.Add(new Vector2((float)(_cx + hist[j]), (float)(_cy + hist[j + 1] * k)));
            }
            else
            {
                _poly.Add(new Vector2((float)(_cx + p.X), (float)(_cy + p.Y * k)));
            }
            _poly.Add(new Vector2((float)(_cx + p.X + p.Vx * ext), (float)(_cy + (p.Y + p.Vy * ext) * k)));

            pt.Grad.HexAt(lf, out byte hr, out byte hg, out byte hb);
            var col = new Vector3(hr / 255f, hg / 255f, hb / 255f);
            double glowSize = pt.GlowSize != 0 && !double.IsNaN(pt.GlowSize) ? pt.GlowSize : 1.5;
            if (pt.Glow > 0.01)
            {
                for (double rem = pt.Glow; rem > 0.01; rem -= 1)
                    StrokePolyline(_poly, width * (1 + glowSize), col, alpha * 0.5 * Math.Min(1, rem), DrawBlend.Add);
            }
            StrokePolyline(_poly, width, col, alpha, blend);
            if (pt.TrailCore > 0.01)
                StrokePolyline(_poly, Math.Max(1, width * 0.35), Vector3.one, alpha * Js.Clamp(pt.TrailCore, 0, 1), blend);
        }

        private void RibbonPush(double x, double y)
        {
            if (_rn >= _rx.Length)
            {
                int cap = _rx.Length * 2;
                Array.Resize(ref _rx, cap); Array.Resize(ref _ry, cap);
                Array.Resize(ref _rnx, cap); Array.Resize(ref _rny, cap); Array.Resize(ref _rhw, cap);
            }
            _rx[_rn] = x; _ry[_rn] = y; _rn++;
        }

        private int RibbonBuild(Particle p, double ext)
        {
            _rn = 0;
            double k = _k;
            List<double> hist = p.Hist;
            if (hist != null)
            {
                for (int j = 0; j < hist.Count; j += 2)
                {
                    double x = _cx + hist[j], y = _cy + hist[j + 1] * k;
                    if (_rn > 0 && Math.Abs(x - _rx[_rn - 1]) < 0.01 && Math.Abs(y - _ry[_rn - 1]) < 0.01) continue;
                    RibbonPush(x, y);
                }
            }
            double hx = _cx + p.X + p.Vx * ext, hy = _cy + (p.Y + p.Vy * ext) * k;
            if (_rn == 0 || Math.Abs(hx - _rx[_rn - 1]) >= 0.01 || Math.Abs(hy - _ry[_rn - 1]) >= 0.01) RibbonPush(hx, hy);
            return _rn;
        }

        private void RibbonShape(Curve curve, double width)
        {
            int n = _rn;
            double len = 0;
            _rhw[0] = 0;
            for (int i = 1; i < n; i++)
            {
                double dx = _rx[i] - _rx[i - 1], dy = _ry[i] - _ry[i - 1];
                len += Math.Sqrt(dx * dx + dy * dy);
                _rhw[i] = len;
            }
            double invLen = len > 1e-6 ? 1 / len : 0;
            for (int i = 0; i < n; i++) _rhw[i] = 0.5 * width * curve.Eval(_rhw[i] * invLen);
            double pdx = 0, pdy = 0;
            for (int i = 0; i < n; i++)
            {
                double d1x, d1y, d2x, d2y;
                if (i > 0) { d1x = _rx[i] - _rx[i - 1]; d1y = _ry[i] - _ry[i - 1]; }
                else { d1x = _rx[1] - _rx[0]; d1y = _ry[1] - _ry[0]; }
                if (i < n - 1) { d2x = _rx[i + 1] - _rx[i]; d2y = _ry[i + 1] - _ry[i]; }
                else { d2x = d1x; d2y = d1y; }
                double l1 = Math.Sqrt(d1x * d1x + d1y * d1y), l2 = Math.Sqrt(d2x * d2x + d2y * d2y);
                if (l1 > 1e-6) { d1x /= l1; d1y /= l1; } else { d1x = pdx; d1y = pdy; }
                if (l2 > 1e-6) { d2x /= l2; d2y /= l2; } else { d2x = d1x; d2y = d1y; }
                pdx = d2x; pdy = d2y;
                double mx = -d1y - d2y, my = d1x + d2x;
                double ml = Math.Sqrt(mx * mx + my * my);
                if (ml < 1e-6) { _rnx[i] = -d1y; _rny[i] = d1x; continue; }
                double ux = mx / ml, uy = my / ml;
                double miter = 1 / Math.Max(0.5, ux * -d1y + uy * d1x);
                _rnx[i] = ux * miter;
                _rny[i] = uy * miter;
            }
        }

        private double RibbonHw(int i, double scale, bool core) =>
            core ? Math.Min(_rhw[i], Math.Max(0.5, _rhw[i] * scale)) : _rhw[i] * scale;

        /// <summary>Контур ленты: левая кромка -> кап головы -> правая кромка -> кап хвоста.</summary>
        private void RibbonPath(double scale, bool core)
        {
            _poly.Clear();
            int n = _rn;
            for (int i = 0; i < n; i++)
            {
                double h = RibbonHw(i, scale, core);
                _poly.Add(new Vector2((float)(_rx[i] + _rnx[i] * h), (float)(_ry[i] + _rny[i] * h)));
            }
            double aH = Math.Atan2(_rny[n - 1], _rnx[n - 1]);
            ArcPoints(_rx[n - 1], _ry[n - 1], RibbonHw(n - 1, scale, core), aH, aH - Math.PI);
            for (int i = n - 2; i >= 0; i--)
            {
                double h = RibbonHw(i, scale, core);
                _poly.Add(new Vector2((float)(_rx[i] - _rnx[i] * h), (float)(_ry[i] - _rny[i] * h)));
            }
            double aT = Math.Atan2(_rny[0], _rnx[0]);
            ArcPoints(_rx[0], _ry[0], RibbonHw(0, scale, core), aT - Math.PI, aT - 2 * Math.PI);
        }

        // ctx.arc(..., anticlockwise=true): угол убывает от a0 до a1
        private void ArcPoints(double x, double y, double r, double a0, double a1)
        {
            int seg = Mathf.Clamp(Mathf.CeilToInt((float)(r * _scale * 0.6)) + 6, 6, 32);
            for (int i = 0; i <= seg; i++)
            {
                double a = a0 + (a1 - a0) * i / seg;
                _poly.Add(new Vector2((float)(x + Math.Cos(a) * r), (float)(y + Math.Sin(a) * r)));
            }
        }

        // =====================================================================
        // СПРАЙТ / АТЛАС
        // =====================================================================

        private void DrawSprite(Layer layer, double t)
        {
            double tl = t - layer.Start;
            double win = Math.Max(0.0001, layer.End - layer.Start);
            if (tl < 0 || tl > win) return;
            SpriteSettings sp = layer.Sp;
            if (sp == null) return;

            double alpha = Js.Clamp(layer.Opacity.Eval(tl), 0, 1);
            if (alpha <= 0.004) return;
            double x = sp.X.Eval(tl), y = sp.Y.Eval(tl);
            double scale = Math.Max(0, sp.Scale.Eval(tl));
            double rot = sp.Rot.Eval(tl) * Js.DEG;
            double glow = Js.Clamp(sp.Glow.Eval(tl), 0, 3);
            sp.Color.EvalColor(tl, out double cr, out double cg, out double cb);
            byte hr = ModelUtil.ToByte(cr), hg = ModelUtil.ToByte(cg), hb = ModelUtil.ToByte(cb);

            double k = _k;
            double squashK = 1 - (1 - k) * Js.Clamp(sp.Squash, 0, 1);
            double px = _cx + x, py = _cy + y * k;
            DrawBlend blend = ToDraw(layer.Blend);
            double aspect = sp.Aspect != 0 && !double.IsNaN(sp.Aspect) ? sp.Aspect : 1;

            bool isTex = sp.Sprite.IsTexture;
            Texture img;
            Vector4 uv;
            float iw, ih;
            bool tint;
            SpriteImage si = default;
            bool shape = false;
            if (isTex)
            {
                EffectTexture tex = Data.GetTexture(sp.Sprite.TexId);
                if (tex == null || tex.Texture == null) return;
                img = tex.Texture;
                if (tex.Info.HasSheet)
                {
                    int total = Math.Max(1, tex.Info.SheetCols * tex.Info.SheetRows);
                    int idx = sp.Fps > 0 ? (int)(Math.Floor(tl * sp.Fps) % total) : Math.Min(total - 1, (int)Math.Floor(tl / win * total));
                    uv = SheetUv(tex, idx, out iw, out ih);
                }
                else
                {
                    uv = new Vector4(0, 1, 1, 0);
                    iw = tex.Width;
                    ih = tex.Height;
                }
                tint = false;
            }
            else
            {
                int v = Math.Max(0, sp.Sprite.Var.HasValue && !double.IsNaN(sp.Sprite.Var.Value) ? (int)sp.Sprite.Var.Value : 0);
                si = SheetFor(sp, sp.Sprite, sp.GlowBlur).Image(v);
                img = si.Texture;
                uv = si.Uv;
                iw = si.Width;
                ih = si.Height;
                tint = true;
                shape = true;
            }

            double bas = sp.Size * scale / Math.Max(iw, ih);
            double sxm = bas * aspect;
            var col = new Vector4(hr / 255f, hg / 255f, hb / 255f, (float)alpha);
            AddQuad(img, blend, Affine.Particle(px, py, rot, sxm, bas, squashK), -iw / 2, -ih / 2, iw / 2, ih / 2, uv, col, tint);

            if (glow > 0.01 && shape)
            {
                double glowSize = sp.GlowSize != 0 && !double.IsNaN(sp.GlowSize) ? sp.GlowSize : 1.5;
                const float gw = 192;
                double gs = sp.Size * scale * glowSize / (gw * 0.5);
                Affine gm = Affine.Particle(px, py, rot, gs * aspect, gs, squashK);
                for (double rem = glow; rem > 0.01; rem -= 1)
                {
                    col.w = (float)(alpha * Math.Min(1, rem));
                    AddQuad(img, DrawBlend.Add, gm, -gw / 2, -gw / 2, gw / 2, gw / 2, si.GlowUv, col, true);
                }
            }
        }

        // =====================================================================
        // ЦЕЛИ, ПАКЕТЫ, ПРИМИТИВЫ
        // =====================================================================

        private RenderTexture _curTarget;
        private double _curW, _curH;

        private void Bind(RenderTexture rt, double coordW, double coordH)
        {
            if (rt == _curTarget && coordW == _curW && coordH == _curH) return;
            FlushBatch();
            _curTarget = rt;
            _curW = coordW;
            _curH = coordH;
            _cmd.SetRenderTarget(rt);
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(0, (float)coordW, (float)coordH, 0, -1, 1), true);
            _cmd.SetGlobalMatrix(Materials.Proj, proj);
        }

        private void BindClear(RenderTexture rt, double coordW, double coordH)
        {
            Bind(rt, coordW, coordH);
            FlushBatch();
            _cmd.ClearRenderTarget(true, true, Color.clear);
        }

        // текущий пакет: одна текстура, один режим наложения
        private readonly List<Vector3> _bPos = new List<Vector3>(1024);
        private readonly List<Vector3> _bUv = new List<Vector3>(1024);
        private readonly List<Vector4> _bCol = new List<Vector4>(1024);
        private readonly List<int> _bIdx = new List<int>(1536);
        private Texture _bTex;
        private DrawBlend _bBlend;

        private void AddQuad(Texture tex, DrawBlend blend, Affine m, float x0, float y0, float x1, float y1, Vector4 uv, Vector4 col, bool tint)
        {
            if (_bPos.Count > 0 && (tex != _bTex || blend != _bBlend || _bPos.Count > 60000)) FlushBatch();
            _bTex = tex;
            _bBlend = blend;
            int b = _bPos.Count;
            float mode = tint ? 1 : 0;
            _bPos.Add(m.Apply(x0, y0)); _bUv.Add(new Vector3(uv.x, uv.y, mode));
            _bPos.Add(m.Apply(x1, y0)); _bUv.Add(new Vector3(uv.z, uv.y, mode));
            _bPos.Add(m.Apply(x1, y1)); _bUv.Add(new Vector3(uv.z, uv.w, mode));
            _bPos.Add(m.Apply(x0, y1)); _bUv.Add(new Vector3(uv.x, uv.w, mode));
            _bCol.Add(col); _bCol.Add(col); _bCol.Add(col); _bCol.Add(col);
            _bIdx.Add(b); _bIdx.Add(b + 1); _bIdx.Add(b + 2);
            _bIdx.Add(b); _bIdx.Add(b + 2); _bIdx.Add(b + 3);
        }

        private void FlushBatch()
        {
            if (_bPos.Count == 0) return;
            Mesh mesh = _pool.Next();
            mesh.SetVertices(_bPos);
            mesh.SetUVs(0, _bUv);
            mesh.SetUVs(1, _bCol);
            mesh.SetIndices(_bIdx, MeshTopology.Triangles, 0, false);
            _mpb.Clear();
            _mpb.SetTexture(Materials.MainTex, _bTex);
            DrawMesh(mesh, _bBlend, StencilMode.None, StencilMode.None);
            _bPos.Clear(); _bUv.Clear(); _bCol.Clear(); _bIdx.Clear();
            _bTex = null;
        }

        private void DrawMesh(Mesh mesh, DrawBlend blend, StencilMode st, StencilMode stLast)
        {
            if (blend == DrawBlend.Multiply)
            {
                _cmd.DrawMesh(mesh, Matrix4x4.identity, Materials.Draw(3, st), 0, 0, _mpb);
                _cmd.DrawMesh(mesh, Matrix4x4.identity, Materials.Draw(4, stLast), 0, 0, _mpb);
                return;
            }
            int b = blend == DrawBlend.Add ? 1 : blend == DrawBlend.Screen ? 2 : blend == DrawBlend.Copy ? 5 : 0;
            _cmd.DrawMesh(mesh, Matrix4x4.identity, Materials.Draw(b, stLast), 0, 0, _mpb);
        }

        /// <summary>Нарисовать текстуру-холст (премультиплицированную) поверх цели на весь кадр композиции.</summary>
        private void DrawFull(RenderTexture target, Texture src, DrawBlend blend, double alpha)
        {
            Bind(target, _cw, _ch);
            AddQuad(src, blend, Affine.Identity, 0, 0, (float)_cw, (float)_ch, new Vector4(0, 1, 1, 0), new Vector4(1, 1, 1, (float)alpha), false);
        }

        private RenderTexture CopyOf(RenderTexture src, bool stencil)
        {
            RenderTexture c = Temp(stencil, 0, 0, stencil ? -1 : 1);
            BindClear(c, _cw, _ch);
            DrawFull(c, src, DrawBlend.Copy, 1);
            return c;
        }

        // ---------- трафаретные фигуры (ленты трейлов) ----------

        private readonly List<Vector3> _sPos = new List<Vector3>(512);
        private readonly List<int> _sIdx = new List<int>(1024);

        /// <summary>Заливка многоугольника по правилу nonzero: трафарет веером + накрытие.</summary>
        private void FillPolygon(List<Vector2> pts, Vector3 rgb, double alpha, DrawBlend blend)
        {
            if (pts.Count < 3 || alpha <= 0) return;
            FlushBatch();
            _sPos.Clear(); _sIdx.Clear();
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 v = pts[i];
                _sPos.Add(v);
                if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x;
                if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y;
            }
            for (int i = 1; i < pts.Count - 1; i++) { _sIdx.Add(0); _sIdx.Add(i); _sIdx.Add(i + 1); }
            StencilCover(StencilMode.WriteNonzero, minX, minY, maxX, maxY, rgb, alpha, blend);
        }

        /// <summary>Штрих ломаной с круглыми стыками и концами (lineJoin/lineCap round).</summary>
        private void StrokePolyline(List<Vector2> pts, double width, Vector3 rgb, double alpha, DrawBlend blend)
        {
            if (pts.Count == 0 || alpha <= 0) return;
            FlushBatch();
            _sPos.Clear(); _sIdx.Clear();
            float h = (float)(width * 0.5);
            int seg = Mathf.Clamp(Mathf.CeilToInt(h * _scale * 0.75f) + 6, 8, 48);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i];
                minX = Mathf.Min(minX, p.x - h); maxX = Mathf.Max(maxX, p.x + h);
                minY = Mathf.Min(minY, p.y - h); maxY = Mathf.Max(maxY, p.y + h);
                // круг в каждой вершине: круглые стыки и концы
                int c = _sPos.Count;
                _sPos.Add(p);
                for (int s = 0; s < seg; s++)
                {
                    float a = s * Mathf.PI * 2 / seg;
                    _sPos.Add(new Vector3(p.x + Mathf.Cos(a) * h, p.y + Mathf.Sin(a) * h, 0));
                }
                for (int s = 0; s < seg; s++) { _sIdx.Add(c); _sIdx.Add(c + 1 + s); _sIdx.Add(c + 1 + (s + 1) % seg); }
                if (i == 0) continue;
                // прямоугольник сегмента
                Vector2 q = pts[i - 1];
                Vector2 d = p - q;
                float l = d.magnitude;
                if (l < 1e-5f) continue;
                Vector2 n = new Vector2(-d.y, d.x) * (h / l);
                int b = _sPos.Count;
                _sPos.Add(q + n); _sPos.Add(p + n); _sPos.Add(p - n); _sPos.Add(q - n);
                _sIdx.Add(b); _sIdx.Add(b + 1); _sIdx.Add(b + 2);
                _sIdx.Add(b); _sIdx.Add(b + 2); _sIdx.Add(b + 3);
            }
            StencilCover(StencilMode.WriteUnion, minX, minY, maxX, maxY, rgb, alpha, blend);
        }

        private void StencilCover(StencilMode write, float minX, float minY, float maxX, float maxY, Vector3 rgb, double alpha, DrawBlend blend)
        {
            Mesh sm = _pool.Next();
            sm.SetVertices(_sPos);
            sm.SetIndices(_sIdx, MeshTopology.Triangles, 0, false);
            _mpb.Clear();
            _mpb.SetTexture(Materials.MainTex, ShapeAtlas.White);
            _cmd.DrawMesh(sm, Matrix4x4.identity, Materials.Draw(0, write), 0, 0, _mpb);

            Mesh cm = _pool.Next();
            var col = new Vector4(rgb.x, rgb.y, rgb.z, (float)alpha);
            _bPos.Clear(); _bUv.Clear(); _bCol.Clear(); _bIdx.Clear();
            minX -= 1; minY -= 1; maxX += 1; maxY += 1;
            _bPos.Add(new Vector3(minX, minY)); _bPos.Add(new Vector3(maxX, minY));
            _bPos.Add(new Vector3(maxX, maxY)); _bPos.Add(new Vector3(minX, maxY));
            for (int i = 0; i < 4; i++) { _bUv.Add(new Vector3(0.5f, 0.5f, 1)); _bCol.Add(col); }
            _bIdx.Add(0); _bIdx.Add(1); _bIdx.Add(2); _bIdx.Add(0); _bIdx.Add(2); _bIdx.Add(3);
            cm.SetVertices(_bPos);
            cm.SetUVs(0, _bUv);
            cm.SetUVs(1, _bCol);
            cm.SetIndices(_bIdx, MeshTopology.Triangles, 0, false);
            _bPos.Clear(); _bUv.Clear(); _bCol.Clear(); _bIdx.Clear();
            DrawMesh(cm, blend, StencilMode.CoverKeep, StencilMode.CoverZero);
        }

        // ---------- полноэкранные проходы ----------

        private Mesh Quad
        {
            get
            {
                if (_quad != null) return _quad;
                _quad = new Mesh { name = "ArcadiaQuad", hideFlags = HideFlags.HideAndDontSave };
                _quad.SetVertices(new List<Vector3> { new Vector3(0, 0), new Vector3(1, 0), new Vector3(1, 1), new Vector3(0, 1) });
                _quad.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
                return _quad;
            }
        }

        private void PostPass(RenderTexture target, double coordW, double coordH, int pass)
        {
            Bind(target, coordW, coordH);
            FlushBatch();
            _cmd.DrawMesh(Quad, Matrix4x4.Scale(new Vector3((float)coordW, (float)coordH, 1)), Materials.Post, 0, pass, _pp);
        }

        private static readonly int CoordId = Shader.PropertyToID("_Coord");
        private static readonly int TexelId = Shader.PropertyToID("_MainTex_TexelSize");
        private static readonly int GridTexelId = Shader.PropertyToID("_Grid_TexelSize");

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
            int f = 1;
            while (sigma / f > 6 && f < 8) f *= 2;
            RenderTexture cur = src;
            int w = _rtW, h = _rtH;
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
            _pp.Clear();
            SetSrc(cur);
            _pp.SetVector(CoordId, new Vector4(w, h, 1, 1));
            _pp.SetVector("_BlurDir", new Vector4(1, 0, 0, 0));
            _pp.SetFloat("_Sigma", sg);
            Bind(a, w, h);
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
        // ПОСТЭФФЕКТЫ
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

        private static PostP PostParams(Layer layer, double t)
        {
            PostFxSettings fx = layer.Fx;
            if (fx == null || fx.Effect == PostEffect.None) return null;
            double tl = t - layer.Start;
            double win = Math.Max(0.0001, layer.End - layer.Start);
            if (tl < 0 || tl > win) return null;
            double mix = Js.Clamp(layer.Opacity.Eval(tl), 0, 1);
            if (mix <= 0.004) return null;
            switch (fx.Effect)
            {
                case PostEffect.Displace:
                {
                    double amount = Math.Max(0, fx.Amount.Eval(tl)) * mix;
                    if (amount < 0.4) return null;
                    int oct = Js.ToInt32(fx.Oct);
                    return new PostP
                    {
                        Effect = PostEffect.Displace, Layer = layer, Mix = mix,
                        Amount = amount,
                        Scale = Math.Max(4, fx.Scale != 0 && !double.IsNaN(fx.Scale) ? fx.Scale : 60),
                        Aspect = Js.Clamp(fx.Aspect != 0 && !double.IsNaN(fx.Aspect) ? fx.Aspect : 1, 0.2, 8),
                        Oct = Js.Clamp(oct != 0 ? oct : 1, 1, 4),
                        Swirl = fx.Swirl,
                        Z = t * fx.Evo,
                        Scroll = -fx.Rise * t,
                        Ramp = Js.Clamp(fx.Ramp, 0, 1),
                        Y0 = fx.Y0.Eval(tl),
                        Soft = Math.Max(1, fx.Soft != 0 && !double.IsNaN(fx.Soft) ? fx.Soft : 120),
                        Seed = (double)Js.ToInt32(fx.Seed) * 3571
                    };
                }
                case PostEffect.Pixelate:
                    return new PostP { Effect = PostEffect.Pixelate, Layer = layer, Mix = mix, Fx = fx, Thr = Js.Clamp(fx.PxThr.Eval(tl), 0, 1) };
                default:
                {
                    double len = Math.Max(0, fx.Length.Eval(tl));
                    bool radial = fx.Radial;
                    double spin = radial ? fx.Spin.Eval(tl) : 0;
                    if (len < 0.5 && Math.Abs(spin) < 0.05) return null;
                    return new PostP
                    {
                        Effect = PostEffect.MotionBlur, Layer = layer, Mix = mix, Radial = radial, Len = len,
                        Angle = fx.Angle.Eval(tl) * Js.DEG,
                        Spin = spin * Js.DEG,
                        X = fx.X.Eval(tl), Y = fx.Y.Eval(tl),
                        Passes = (int)Js.Clamp(Js.Round(Math.Log(Math.Max(2, Js.ToInt32(fx.Samples))) / Math.Log(2)), 1, 6),
                        Half = fx.Half
                    };
                }
            }
        }

        private void ApplyPost(RenderTexture x, PostP p)
        {
            if (p == null) return;
            switch (p.Effect)
            {
                case PostEffect.Displace: ApplyDisplace(x, p); break;
                case PostEffect.Pixelate: ApplyPixelate(x, p); break;
                default: ApplyMotionBlur(x, p); break;
            }
        }

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
                double R = Math.Max(1, Hypot(Math.Max(pcx, bw - pcx), Math.Max(pcy, bh - pcy)));
                double u = p.Len * q / R;
                step = Math.Pow(Math.Pow((u + Math.Sqrt(u * u + 4)) / 2, 2), 1.0 / (n - 1));
                rot = p.Spin / (n - 1);
            }
            else
            {
                dx = Math.Cos(p.Angle) * p.Len * q / (n - 1);
                dy = Math.Sin(p.Angle) * p.Len * q / (n - 1);
            }

            RenderTexture orig = CopyOf(x, false);
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

        private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

        private static readonly int GridId = Shader.PropertyToID("_Grid");

        private void ApplyDisplace(RenderTexture x, PostP p)
        {
            int cw = (int)Math.Round(_cw), ch = (int)Math.Round(_ch);
            int gsStep = (int)Math.Max(2, Math.Min(12, Js.Round(p.Scale / 16)));
            int gw = (int)Math.Ceiling((double)cw / gsStep) + 2, gh = (int)Math.Ceiling((double)ch / gsStep) + 2;
            var data = new float[gw * gh * 2];
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
            Texture2D grid = GridTexture(p.Layer, gw, gh, data);

            RenderTexture src = CopyOf(x, false);
            BindClear(x, _cw, _ch);
            _pp.Clear();
            SetSrc(src);
            _pp.SetTexture(GridId, grid);
            _pp.SetVector(GridTexelId, new Vector4(1f / grid.width, 1f / grid.height, grid.width, grid.height));
            _pp.SetFloat("_GridStep", gsStep);
            _pp.SetVector(CoordId, new Vector4((float)_cw, (float)_ch, _scale, _scale));
            PostPass(x, _cw, _ch, 2);
        }

        private Texture2D GridTexture(Layer owner, int gw, int gh, float[] data)
        {
            bool fl = SystemInfo.SupportsTextureFormat(TextureFormat.RGFloat);
            if (!_grids.TryGetValue(owner, out Texture2D tex) || tex == null || tex.width != gw || tex.height != gh)
            {
                if (tex != null) MeshPool.Destroy(tex);
                tex = new Texture2D(gw, gh, fl ? TextureFormat.RGFloat : TextureFormat.RGHalf, false, true)
                {
                    name = "ArcadiaDisplaceGrid", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                _grids[owner] = tex;
            }
            if (fl) tex.SetPixelData(data, 0);
            else
            {
                var h = new ushort[data.Length];
                for (int i = 0; i < data.Length; i++) h[i] = Mathf.FloatToHalf(data[i]);
                tex.SetPixelData(h, 0);
            }
            tex.Apply(false, false);
            return tex;
        }

        private static float[] _bayerCache2, _bayerCache4, _bayerCache8;

        private static float[] Bayer(int n)
        {
            ref float[] cache = ref _bayerCache2;
            if (n == 4) cache = ref _bayerCache4;
            else if (n == 8) cache = ref _bayerCache8;
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

            RenderTexture src = CopyOf(x, false);
            RenderTexture a = Temp(false, g.Gw, g.Gh, 1, FilterMode.Point);
            RenderTexture b = Temp(false, g.Gw, g.Gh, 1, FilterMode.Point);

            _pp.Clear();
            SetSrc(src);
            _pp.SetVector(CoordId, new Vector4(g.Gw, g.Gh, 1, 1));
            _pp.SetVector("_PxGrid", new Vector4((float)g.Bx, (float)g.By, (float)g.Ox, (float)g.Oy));
            _pp.SetFloat("_PxScale", _scale);
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
