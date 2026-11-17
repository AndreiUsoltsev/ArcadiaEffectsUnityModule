using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Рендер композиции эффекта в момент t — порт js/core/engine.js.
    /// <para>
    /// Слои, которым нужен холст (свечение слоя, затухание, screen/multiply, постэффекты и всё,
    /// что под ними), рисуются в <see cref="Output"/> (RenderTexture, премультиплицированная альфа,
    /// значения в гамме — как Canvas2D). На уровнях качества с <c>directLayers</c> слои над ними
    /// собираются в <see cref="DisplayMesh"/> и рисуются прямо в сцену, без RenderTexture.
    /// </para>
    /// У каждого экземпляра свои симуляции (как у каждого Engine в редакторе).
    /// </summary>
    public sealed partial class ArcadiaEffectRenderer : IDisposable
    {
        private static readonly ProfilerMarker MarkRecord = new ProfilerMarker("ArcadiaEffects.Record");
        private static readonly ProfilerMarker MarkSim = new ProfilerMarker("ArcadiaEffects.Simulate");
        private static readonly ProfilerMarker MarkPost = new ProfilerMarker("ArcadiaEffects.PostFx");
        private static readonly ProfilerMarker MarkUpload = new ProfilerMarker("ArcadiaEffects.Upload");

        public ArcadiaEffectData Data { get; }
        /// <summary>Итоговый уровень качества (Auto уже разрешён).</summary>
        public ArcadiaQuality Quality { get; }
        public ArcadiaQualityProfile Profile { get; }

        /// <summary>
        /// Холст композиции (null — эффекту холст не нужен или он освобождён).
        /// Содержит слои ниже первой «прямой» группы.
        /// </summary>
        public RenderTexture Output { get; private set; }

        /// <summary>Нужен ли эффекту холст (есть слои, которые нельзя рисовать прямо в сцену).</summary>
        public bool UsesOutput => _split < _layerCount;

        /// <summary>Есть слои, рисуемые прямо в сцену (<see cref="DisplayMesh"/>).</summary>
        public bool HasDirect => _split > 0;

        /// <summary>
        /// Меш для показа в сцене: сабмеш на холст (если он есть) и сабмеши прямых слоёв.
        /// Вершины — в пикселях композиции (Y вниз), в локальные юниты их переводит шейдер
        /// <c>ArcadiaEffects/Display</c> по свойству <c>_Comp</c>.
        /// </summary>
        public Mesh DisplayMesh => _direct.Mesh;

        /// <summary>Число сабмешей <see cref="DisplayMesh"/> на последнем кадре.</summary>
        public int DisplaySubMeshCount => _directTextures.Count;

        /// <summary>Текстура сабмеша <see cref="DisplayMesh"/> (холст или текстура частиц).</summary>
        public Texture DisplayTexture(int subMesh) => _directTextures[subMesh];

        /// <summary>Пикселей композиции на юнит — для границ <see cref="DisplayMesh"/>.</summary>
        public float DisplayPixelsPerUnit { get; set; } = 100f;

        /// <summary>Число живых частиц на последнем кадре.</summary>
        public int ParticleCount { get; private set; }

        /// <summary>Масштаб холста относительно композиции (ограничен уровнем качества).</summary>
        public float ResolutionScale
        {
            get => _scale;
            set
            {
                float s = Mathf.Clamp(value, 0.05f, Mathf.Max(0.05f, Profile.maxResolutionScale));
                if (Mathf.Approximately(s, _scale)) return;
                _scale = s;
                ReleaseOutput();
            }
        }

        private float _scale;
        private int _msaa;
        private readonly bool _stencil;
        private readonly bool _stencilTrails;
        private readonly int _layerCount;
        private readonly int _split; // слои [0, _split) — прямые, [_split, N) — на холсте
        private int _rtW, _rtH;

        private readonly CommandBuffer _ownCmd = new CommandBuffer { name = "ArcadiaEffect" };
        private CommandBuffer _cmd;
        private readonly GeometryBuffer _frame = new GeometryBuffer("ArcadiaFrame");
        private readonly GeometryBuffer _direct = new GeometryBuffer("ArcadiaDisplay");
        private readonly List<Texture> _directTextures = new List<Texture>();
        private readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();   // пакеты
        private readonly MaterialPropertyBlock _pp = new MaterialPropertyBlock();    // полноэкранные проходы
        private readonly Dictionary<string, Simulation> _sims = new Dictionary<string, Simulation>();
        private readonly Dictionary<object, ShapeAtlas.Sheet> _sheets = new Dictionary<object, ShapeAtlas.Sheet>();
        private readonly List<RenderTexture> _temps = new List<RenderTexture>();
        private readonly List<int> _vis = new List<int>();
        private Mesh _quad;
        private bool _disposed;
        private bool _toDirect;
        private readonly bool _display;
        internal int LastRenderedFrame = -1;

        private double _cw, _ch, _cx, _cy, _k;

        /// <param name="data">Загруженный эффект.</param>
        /// <param name="quality">Уровень качества (Auto — по настройкам и устройству).</param>
        /// <param name="resolutionScale">Масштаб холста (не выше <c>maxResolutionScale</c> уровня).</param>
        /// <param name="sceneDisplay">
        /// Показывать через <see cref="DisplayMesh"/> (компонент в мире): тогда строится меш показа
        /// и разрешены прямые слои. False — только холст <see cref="Output"/> (UI, свой материал).
        /// </param>
        public ArcadiaEffectRenderer(ArcadiaEffectData data, ArcadiaQuality quality = ArcadiaQuality.Auto,
            float resolutionScale = 1f, bool sceneDisplay = false)
        {
            Data = data ?? throw new ArgumentNullException(nameof(data));
            Quality = ArcadiaEffectsSettings.Resolve(quality);
            Profile = ArcadiaEffectsSettings.Profile(Quality);
            EffectDocument doc = data.Document;
            _cw = doc.CompW;
            _ch = doc.CompH;
            _scale = Mathf.Clamp(resolutionScale, 0.05f, Mathf.Max(0.05f, Profile.maxResolutionScale));
            _layerCount = doc.Layers.Count;
            _stencilTrails = Profile.stencilTrails;
            _stencil = doc.Features.HasTrails && _stencilTrails;
            _msaa = _stencil ? Mathf.Clamp(Mathf.ClosestPowerOfTwo(Mathf.Max(1, Profile.trailMsaa)), 1, 8) : 1;

            // ПРЯМЫЕ СЛОИ: всё выше самого верхнего слоя, которому нужен холст
            _display = sceneDisplay;
            _split = 0;
            if (sceneDisplay && Profile.directLayers)
            {
                _split = _layerCount;
                for (int i = 0; i < _layerCount; i++)
                {
                    Layer l = doc.Layers[i];
                    if (l.On && !l.CanDrawDirect(!_stencilTrails)) { _split = i; break; }
                }
            }
            PrepareGroups();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseTemps();
            ReleaseOutput();
            _frame.Dispose();
            _direct.Dispose();
            DisposePost();
            MeshPool.Destroy(_quad);
            _ownCmd.Release();
        }

        /// <summary>Сбросить симуляции (следующий кадр пересчитает всё с нуля).</summary>
        public void ResetSimulations()
        {
            foreach (var s in _sims.Values) s.Reset();
        }

        /// <summary>Отпустить холст (эффект скрыт или закончил); следующий кадр возьмёт новый.</summary>
        public void ReleaseOutput()
        {
            if (Output == null) return;
            RenderTexture.ReleaseTemporary(Output);
            Output = null;
        }

        /// <summary>Очистить показ (холст в прозрачный, прямые слои — пусто).</summary>
        public void Clear()
        {
            _direct.Reset();
            _directTextures.Clear();
            if (Output == null) return;
            _ownCmd.Clear();
            _ownCmd.SetRenderTarget(Output);
            _ownCmd.ClearRenderTarget(true, true, Color.clear);
            Graphics.ExecuteCommandBuffer(_ownCmd);
            _ownCmd.Clear();
        }

        private RenderTextureDescriptor CanvasDesc(bool stencil, int w, int h, int msaa, RenderTextureFormat fmt = RenderTextureFormat.ARGB32)
        {
            return new RenderTextureDescriptor(w, h, fmt, stencil ? 24 : 0)
            {
                sRGB = false,
                msaaSamples = msaa,
                useMipMap = false,
                autoGenerateMips = false
            };
        }

        /// <summary>Временный холст размера композиции (со своим трафаретом, если эффекту он нужен).</summary>
        private RenderTexture TempCanvas(bool forDrawing) => Temp(forDrawing && _stencil, _rtW, _rtH, forDrawing ? _msaa : 1);

        private RenderTexture Temp(bool stencil, int w, int h, int msaa, FilterMode filter = FilterMode.Bilinear,
            RenderTextureFormat fmt = RenderTextureFormat.ARGB32)
        {
            var rt = RenderTexture.GetTemporary(CanvasDesc(stencil, w, h, msaa, fmt));
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

        private void EnsureOutput()
        {
            _rtW = Mathf.Max(1, Mathf.RoundToInt((float)(_cw * _scale)));
            _rtH = Mathf.Max(1, Mathf.RoundToInt((float)(_ch * _scale)));
            if (Output != null && (Output.width != _rtW || Output.height != _rtH)) ReleaseOutput();
            if (Output != null) return;
            if (_msaa > 1) _msaa = Mathf.Max(1, Mathf.Min(_msaa, SystemInfo.GetRenderTextureSupportedMSAASampleCount(CanvasDesc(_stencil, _rtW, _rtH, _msaa))));
            Output = RenderTexture.GetTemporary(CanvasDesc(_stencil, _rtW, _rtH, _msaa));
            Output.name = "Arcadia_" + Data.Document.Name;
            Output.filterMode = FilterMode.Bilinear;
            Output.wrapMode = TextureWrapMode.Clamp;
        }

        // =====================================================================
        // КАДР
        // =====================================================================

        /// <summary>Отрисовать композицию в момент <paramref name="t"/> сразу (свой CommandBuffer).</summary>
        public int Render(double t)
        {
            if (_disposed || !Materials.Ready) return 0;
            _ownCmd.Clear();
            int n = Record(_ownCmd, t);
            Graphics.ExecuteCommandBuffer(_ownCmd);
            _ownCmd.Clear();
            EndFrame();
            return n;
        }

        /// <summary>
        /// Записать кадр в общий CommandBuffer (см. <see cref="ArcadiaEffectsSystem"/>).
        /// После его выполнения обязательно вызвать <see cref="EndFrame"/>.
        /// </summary>
        public int Record(CommandBuffer cmd, double t)
        {
            if (_disposed || !Materials.Ready) return 0;
            using (MarkRecord.Auto())
            {
                EffectDocument doc = Data.Document;
                _cmd = cmd;
                _cw = doc.CompW; _ch = doc.CompH;
                _cx = _cw / 2; _cy = _ch / 2;
                _k = doc.CompK;
                _frame.Reset();
                _direct.Reset();
                _directTextures.Clear();
                _curTarget = null;
                _bTex = null;
                _bDepth = false;
                _strokeK = 0;
                int count = 0;

                // видимость с учётом solo (индексы doc.Layers)
                _vis.Clear();
                bool anySolo = false;
                foreach (var l in doc.Layers) if (l.Solo && l.On) { anySolo = true; break; }
                for (int i = 0; i < doc.Layers.Count; i++)
                {
                    Layer l = doc.Layers[i];
                    if (l.On && (!anySolo || l.Solo)) _vis.Add(i);
                }

                // --- холст: слои от нижнего до _split ---
                if (UsesOutput)
                {
                    EnsureOutput();
                    _toDirect = false;
                    BindClear(Output, _cw, _ch);
                    ComputeGroups(doc, t);
                    for (int vi = _vis.Count - 1; vi >= 0; vi--)
                    {
                        int li = _vis[vi];
                        if (li < _split) break;
                        Layer layer = doc.Layers[li];
                        if (_claimedBy[li] >= 0) continue;
                        if (layer.Type == LayerType.Force) continue;
                        if (layer.Type == LayerType.PostFx)
                        {
                            if (_groupCount[li] > 0)
                            {
                                RenderTexture g = TempCanvas(true);
                                BindClear(g, _cw, _ch);
                                // адресаты группы — снизу вверх
                                for (int q = doc.Layers.Count - 1; q > li; q--)
                                    if (_claimedBy[q] == li) count += DrawLayer(doc.Layers[q], t, g);
                                ApplyPost(g, _postParams[li]);
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
                    // холст — первым сабмешем показа
                    if (_display)
                    {
                        _direct.AddQuad(new Vector3(0, 0), new Vector3((float)_cw, 0), new Vector3((float)_cw, (float)_ch), new Vector3(0, (float)_ch),
                            new Vector4(0, 1, 1, 0), VtxMode.Tex, new Vector4(1, 1, 1, 1));
                        _direct.Close();
                        _directTextures.Add(Output);
                    }
                }

                // --- прямые слои: выше _split, прямо в меш показа ---
                if (HasDirect)
                {
                    _toDirect = true;
                    for (int vi = _vis.Count - 1; vi >= 0; vi--)
                    {
                        int li = _vis[vi];
                        if (li >= _split) continue;
                        Layer layer = doc.Layers[li];
                        if (layer.Type == LayerType.Force || layer.Type == LayerType.PostFx) continue;
                        if (layer.Type == LayerType.Emitter) count += DrawEmitter(layer, t);
                        else DrawSprite(layer, t);
                    }
                    CloseDirect();
                    _toDirect = false;
                }

                using (MarkUpload.Auto())
                {
                    _frame.Upload(new Bounds(Vector3.zero, Vector3.one * 1e6f), false);
                    if (_display)
                    {
                        float ppu = Mathf.Max(0.0001f, DisplayPixelsPerUnit);
                        _direct.Upload(new Bounds(Vector3.zero, new Vector3((float)(_cw / ppu), (float)(_ch / ppu), 0.01f)), true);
                    }
                }
                ParticleCount = count;
                _cmd = null;
                return count;
            }
        }

        /// <summary>Конец кадра: отпустить временные текстуры (после выполнения CommandBuffer).</summary>
        public void EndFrame() => ReleaseTemps();

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

        // ---------- группы адресованных корректоров (postGroups), без аллокаций в кадре ----------

        private int[] _claimedBy;            // слой -> индекс корректора, забравшего его (-1 — никто)
        private int[] _groupCount;           // корректор -> число адресатов в этом кадре
        private PostP[] _postParams;         // корректор -> параметры этого кадра
        private HashSet<string>[] _targetSets;
        private bool _anyGroups;

        private void PrepareGroups()
        {
            var layers = Data.Document.Layers;
            int n = layers.Count;
            _claimedBy = new int[n];
            _groupCount = new int[n];
            _postParams = new PostP[n];
            _targetSets = new HashSet<string>[n];
            for (int i = 0; i < n; i++)
            {
                _claimedBy[i] = -1;
                if (layers[i].Type == LayerType.PostFx && layers[i].HasTargets)
                {
                    _targetSets[i] = new HashSet<string>(layers[i].Targets);
                    _anyGroups = true;
                }
            }
        }

        private void ComputeGroups(EffectDocument doc, double t)
        {
            var layers = doc.Layers;
            for (int i = 0; i < layers.Count; i++) { _claimedBy[i] = -1; _groupCount[i] = 0; }
            if (!_anyGroups) return;
            // снизу вверх по видимым слоям: адресат забирает ближайший сверху корректор
            for (int vi = _vis.Count - 1; vi >= 0; vi--)
            {
                int i = _vis[vi];
                Layer fl = layers[i];
                if (_targetSets[i] == null) continue;
                PostP pp = PostParams(fl, t);
                if (pp == null) continue;
                _postParams[i] = pp;
                for (int vj = vi + 1; vj < _vis.Count; vj++)
                {
                    int j = _vis[vj];
                    Layer c = layers[j];
                    if (c.Type == LayerType.PostFx || c.Type == LayerType.Force) continue;
                    if (_claimedBy[j] >= 0 || !_targetSets[i].Contains(c.Id)) continue;
                    _claimedBy[j] = i;
                    _groupCount[i]++;
                }
            }
        }

        /// <summary>Один слой композиции на холсте (<c>drawLayer</c>). Возвращает число частиц.</summary>
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

            RenderTexture lay = TempCanvas(true);
            BindClear(lay, _cw, _ch);
            if (layer.Type == LayerType.Emitter) count += DrawEmitter(layer, t);
            else DrawSprite(layer, t);

            if (fadeOn) RadialFade(lay, layer, tl);

            DrawFull(target, lay, ToDraw(layer.Blend), 1);

            if (Profile.layerGlow && gAmt > 0.01)
            {
                double r = layer.GlowLR != 0 && !double.IsNaN(layer.GlowLR) ? layer.GlowLR : 24;
                RenderTexture blur = BlurOf(lay, Math.Max(1, r));
                for (double rem = gAmt; rem > 0.01; rem -= 1)
                    DrawFull(target, blur, DrawBlend.Add, Math.Min(1, rem));
            }
            return count;
        }

        // =====================================================================
        // ЭМИТТЕР
        // =====================================================================

        private Simulation SimFor(Layer layer)
        {
            if (!_sims.TryGetValue(layer.Id, out var sim))
            {
                sim = new Simulation(layer, Data.Document.ForcesFor(layer.Id), Profile.maxParticles);
                _sims[layer.Id] = sim;
            }
            return sim;
        }

        private int DrawEmitter(Layer layer, double t)
        {
            double tl = t - layer.Start;
            if (tl < 0) return 0;
            Simulation sim = SimFor(layer);
            double ext;
            using (MarkSim.Auto()) ext = sim.Ensure(tl);
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
                s = Data.FindSheet(r, glowBlur) ?? ShapeAtlas.GetSheet(r, glowBlur);
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
                for (double rem = pt.Glow; rem > 0.01; rem -= 1)
                    FillRibbon(1 + glowSize, false, col, alpha * 0.5 * Math.Min(1, rem), DrawBlend.Add);
            }
            FillRibbon(1, false, col, alpha, blend);
            if (pt.TrailCore > 0.01)
                FillRibbon(0.35, true, Vector3.one, alpha * Js.Clamp(pt.TrailCore, 0, 1), blend);
        }

        private void FillRibbon(double scale, bool core, Vector3 col, double alpha, DrawBlend blend)
        {
            if (_stencilTrails && !_toDirect) RibbonUnion(scale, core, col, alpha, blend);
            else
            {
                RibbonStrip(scale, core, col, alpha, blend);
            }
        }

        private void DrawTrailUniform(Particle p, ParticleSettings pt, double lf, double alpha, double width, double ext, DrawBlend blend)
        {
            _poly.Clear();
            double k = _k;
            // точки пути ближе ~пикселя холста ничего не меняют в картинке — прореживаем сразу
            float min2 = MinStep * MinStep;
            List<double> hist = p.Hist;
            if (hist != null && hist.Count >= 2)
            {
                for (int j = 0; j < hist.Count; j += 2)
                {
                    var v = new Vector2((float)(_cx + hist[j]), (float)(_cy + hist[j + 1] * k));
                    if (_poly.Count == 0 || (v - _poly[_poly.Count - 1]).sqrMagnitude >= min2) _poly.Add(v);
                }
            }
            else
            {
                _poly.Add(new Vector2((float)(_cx + p.X), (float)(_cy + p.Y * k)));
            }
            // голова (экстраполированная позиция) — всегда последняя точка
            var head = new Vector2((float)(_cx + p.X + p.Vx * ext), (float)(_cy + (p.Y + p.Vy * ext) * k));
            if (_poly.Count > 1 && (head - _poly[_poly.Count - 1]).sqrMagnitude < min2) _poly[_poly.Count - 1] = head;
            else _poly.Add(head);

            pt.Grad.HexAt(lf, out byte hr, out byte hg, out byte hb);
            var col = new Vector3(hr / 255f, hg / 255f, hb / 255f);
            double glowSize = pt.GlowSize != 0 && !double.IsNaN(pt.GlowSize) ? pt.GlowSize : 1.5;
            if (pt.Glow > 0.01)
            {
                for (double rem = pt.Glow; rem > 0.01; rem -= 1)
                    Stroke(_poly, width * (1 + glowSize), col, alpha * 0.5 * Math.Min(1, rem), DrawBlend.Add);
            }
            Stroke(_poly, width, col, alpha, blend);
            if (pt.TrailCore > 0.01)
                Stroke(_poly, Math.Max(1, width * 0.35), Vector3.one, alpha * Js.Clamp(pt.TrailCore, 0, 1), blend);
        }

        private void Stroke(List<Vector2> pts, double width, Vector3 col, double alpha, DrawBlend blend)
        {
            if (_stencilTrails && !_toDirect) StrokeUnion(pts, width, col, alpha, blend);
            else StrokeStrip(pts, width, col, alpha, blend);
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

        /// <summary>Шаг прореживания пути трейла: ~0.75 пикселя холста (в px композиции).</summary>
        private float MinStep => 0.75f / Mathf.Max(0.05f, _scale);

        private int RibbonBuild(Particle p, double ext)
        {
            _rn = 0;
            double k = _k;
            double step = Math.Max(0.01, MinStep);
            List<double> hist = p.Hist;
            if (hist != null)
            {
                for (int j = 0; j < hist.Count; j += 2)
                {
                    double x = _cx + hist[j], y = _cy + hist[j + 1] * k;
                    if (_rn > 0 && Math.Abs(x - _rx[_rn - 1]) < step && Math.Abs(y - _ry[_rn - 1]) < step) continue;
                    RibbonPush(x, y);
                }
            }
            double hx = _cx + p.X + p.Vx * ext, hy = _cy + (p.Y + p.Vy * ext) * k;
            if (_rn == 0 || Math.Abs(hx - _rx[_rn - 1]) >= 0.01 || Math.Abs(hy - _ry[_rn - 1]) >= 0.01)
            {
                // голова всегда последняя; слишком близкую предыдущую точку заменяем
                if (_rn > 1 && Math.Abs(hx - _rx[_rn - 1]) < step && Math.Abs(hy - _ry[_rn - 1]) < step) _rn--;
                RibbonPush(hx, hy);
            }
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

        /// <summary>Лента полосой без трафарета (Low и прямые слои): пары кромок по точкам пути.</summary>
        private void RibbonStrip(double scale, bool core, Vector3 rgb, double alpha, DrawBlend blend)
        {
            if (alpha <= 0 || _rn < 2) return;
            HalfColor col = HalfColor.Pack(new Vector4(rgb.x, rgb.y, rgb.z, (float)alpha));
            BeginTris(ShapeAtlas.White, blend);
            GeometryBuffer g = CurGeo;
            float mode = ModeFor(true, blend);
            int prev = -1;
            for (int i = 0; i < _rn; i++)
            {
                double h = RibbonHw(i, scale, core);
                int a = g.AddVertex((float)(_rx[i] + _rnx[i] * h), (float)(_ry[i] + _rny[i] * h), 0.5f, 0.5f, mode, col);
                g.AddVertex((float)(_rx[i] - _rnx[i] * h), (float)(_ry[i] - _rny[i] * h), 0.5f, 0.5f, mode, col);
                if (prev >= 0)
                {
                    g.AddTri(prev, prev + 1, a + 1);
                    g.AddTri(prev, a + 1, a);
                }
                prev = a;
            }
        }

        /// <summary>Штрих ломаной полосой без трафарета: прямоугольник на сегмент.</summary>
        private void StrokeStrip(List<Vector2> pts, double width, Vector3 rgb, double alpha, DrawBlend blend)
        {
            if (pts.Count < 2 || alpha <= 0) return;
            HalfColor col = HalfColor.Pack(new Vector4(rgb.x, rgb.y, rgb.z, (float)alpha));
            BeginTris(ShapeAtlas.White, blend);
            GeometryBuffer g = CurGeo;
            float mode = ModeFor(true, blend);
            float h = (float)(width * 0.5);
            for (int i = 1; i < pts.Count; i++)
            {
                Vector2 q = pts[i - 1], p = pts[i];
                Vector2 d = p - q;
                float l = d.magnitude;
                if (l < 1e-5f) continue;
                Vector2 n = new Vector2(-d.y, d.x) * (h / l);
                Vector2 e = d * (h / l * (i == pts.Count - 1 ? 1f : 0.5f));
                Vector2 s = i == 1 ? d * (h / l) : Vector2.zero;
                int b = g.AddVertex(q.x + n.x - s.x, q.y + n.y - s.y, 0.5f, 0.5f, mode, col);
                g.AddVertex(p.x + n.x + e.x, p.y + n.y + e.y, 0.5f, 0.5f, mode, col);
                g.AddVertex(p.x - n.x + e.x, p.y - n.y + e.y, 0.5f, 0.5f, mode, col);
                g.AddVertex(q.x - n.x - s.x, q.y - n.y - s.y, 0.5f, 0.5f, mode, col);
                g.AddTri(b, b + 1, b + 2);
                g.AddTri(b, b + 2, b + 3);
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
    }
}
