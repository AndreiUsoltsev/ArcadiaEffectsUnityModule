using System;
using System.Collections.Generic;
using System.Globalization;

namespace InteractiveSoftware.ArcadiaEffects
{
    public enum LayerType { Emitter, Sprite, Atlas, PostFx, Force }

    public enum LayerBlend { Normal, Add, Screen, Multiply }

    /// <summary>
    /// Документ эффекта Arcaidia Effector (файл <c>library/*.json</c>), разобранный и дозаполненный
    /// дефолтами ровно как <c>Model.migrate</c> + <c>Model.hydrate</c> редактора.
    /// Неизменяем после загрузки: один документ можно проигрывать многими экземплярами.
    /// </summary>
    public sealed class EffectDocument
    {
        public string Id;
        public string Name;
        public int Version;
        public double CompW = 512, CompH = 512, Duration = 1.2, Fps = 60;
        /// <summary>Наклон камеры, градусы (cam.comp). Y рендера плющится на cos.</summary>
        public double CameraTilt;
        public readonly List<TextureInfo> Textures = new List<TextureInfo>();
        /// <summary>Слои; индекс 0 — ВЕРХНИЙ (рисуется последним).</summary>
        public readonly List<Layer> Layers = new List<Layer>();
        /// <summary>Пейлоады текстур: id -> data URL.</summary>
        public readonly Dictionary<string, string> TexturePayloads = new Dictionary<string, string>();

        /// <summary>emitterId -> force-слои, действующие на него (<c>Model.forceIndex</c>).</summary>
        internal readonly Dictionary<string, List<Layer>> ForceIndex = new Dictionary<string, List<Layer>>();

        /// <summary>Компрессия Y: cos(наклона камеры).</summary>
        public double CompK => Math.Cos(Js.Clamp(CameraTilt, 0, 75) * Js.DEG);

        /// <summary>Возможности документа (что дорого рендерить), вычисляются один раз при загрузке.</summary>
        public EffectFeatures Features { get; private set; } = new EffectFeatures();

        /// <summary>Force-слои, действующие на эмиттер; null — полей нет.</summary>
        public List<Layer> ForcesFor(string emitterId) =>
            emitterId != null && ForceIndex.TryGetValue(emitterId, out var list) ? list : null;

        public TextureInfo FindTexture(string id)
        {
            if (id == null) return null;
            for (int i = 0; i < Textures.Count; i++) if (Textures[i].Id == id) return Textures[i];
            return null;
        }

        /// <summary>Разбор файла эффекта (<c>{app, v, doc, tex}</c>) или голого документа.</summary>
        public static EffectDocument Parse(string json)
        {
            object root = Json.Parse(json);
            if (!(root is Dictionary<string, object> file)) throw new FormatException("Effect file must be a JSON object");
            Dictionary<string, object> d = file;
            if (file.TryGetValue("doc", out object docO) && docO is Dictionary<string, object> dd) d = dd;
            var doc = new EffectDocument();
            doc.Read(d);
            if (file.TryGetValue("tex", out object texO) && texO is Dictionary<string, object> tex)
            {
                foreach (var kv in tex)
                    if (kv.Value is string s) doc.TexturePayloads[kv.Key] = s;
            }
            doc.BuildForceIndex();
            doc.Features = EffectFeatures.Of(doc);
            return doc;
        }

        private void Read(Dictionary<string, object> d)
        {
            Version = Js.ToInt32(ModelUtil.Num(d, "v", 0));
            Id = ModelUtil.Str(d, "id", "fx");
            Name = ModelUtil.Str(d, "name", "New Effect");
            var comp = ModelUtil.Obj(d, "comp");
            CompW = ModelUtil.Num(comp, "w", 512);
            CompH = ModelUtil.Num(comp, "h", 512);
            Duration = ModelUtil.Num(comp, "dur", 1.2);
            Fps = ModelUtil.Num(comp, "fps", 60);
            CameraTilt = ModelUtil.Num(ModelUtil.Obj(d, "cam"), "comp", 0);

            foreach (var t in ModelUtil.Arr(d, "textures"))
            {
                if (!(t is Dictionary<string, object> to)) continue;
                var info = new TextureInfo
                {
                    Id = ModelUtil.Str(to, "id", null),
                    Name = ModelUtil.Str(to, "name", "")
                };
                var sh = ModelUtil.Obj(to, "sheet");
                if (sh != null)
                {
                    info.HasSheet = true;
                    info.SheetCols = Math.Max(1, Js.ToInt32(ModelUtil.Num(sh, "cols", 1)));
                    info.SheetRows = Math.Max(1, Js.ToInt32(ModelUtil.Num(sh, "rows", 1)));
                    info.SheetFps = ModelUtil.Num(sh, "fps", 0);
                }
                Textures.Add(info);
            }

            bool legacyStep = Version < 2; // v<2 не знал про em.subStep: пошаговая эмиссия
            foreach (var lo in ModelUtil.Arr(d, "layers"))
            {
                if (!(lo is Dictionary<string, object> l)) continue;
                Layers.Add(Layer.Read(l, this, legacyStep));
            }
        }

        private void BuildForceIndex()
        {
            // снизу вверх по смыслу не важно: ускорения складываются
            for (int i = 0; i < Layers.Count; i++)
            {
                Layer fl = Layers[i];
                if (fl.Type != LayerType.Force || !fl.On || fl.Fields.Length == 0) continue;
                HashSet<string> tg = fl.Targets.Length > 0 ? new HashSet<string>(fl.Targets) : null;
                for (int j = i + 1; j < Layers.Count; j++)
                {
                    Layer em = Layers[j];
                    if (em.Type != LayerType.Emitter || !em.On) continue;
                    if (tg != null && !tg.Contains(em.Id)) continue;
                    if (!ForceIndex.TryGetValue(em.Id, out var list)) ForceIndex[em.Id] = list = new List<Layer>();
                    list.Add(fl);
                }
            }
        }

        // ---------- пиксель-арт ----------

        /// <summary>Сетка пиксель-арта (<c>Model.pixelGridOf</c>) в пикселях композиции.</summary>
        public PixelGrid PixelGridOf(PostFxSettings fx)
        {
            double cw = CompW, ch = CompH;
            double want = Js.Clamp(Or(Js.ToInt32(fx.PxW), 64), 2, 2048);
            double bx = cw / want;
            bx = !fx.PxSnap ? Math.Max(0.5, bx) : Math.Max(1, Js.Round(bx));
            double by = Math.Max(0.5, bx * Js.Clamp(Or(fx.PxAspect, 1), 0.25, 4));
            double offX = Js.Clamp(fx.PxOffX, -0.5, 0.5), offY = Js.Clamp(fx.PxOffY, -0.5, 0.5);
            int gw = Math.Max(1, (int)Math.Ceiling(cw / bx - 1e-6) + (offX != 0 ? 1 : 0));
            int gh = Math.Max(1, (int)Math.Ceiling(ch / by - 1e-6) + (offY != 0 ? 1 : 0));
            return new PixelGrid
            {
                Gw = gw, Gh = gh, Bx = bx, By = by,
                Ox = (cw - gw * bx) / 2 + offX * bx,
                Oy = (ch - gh * by) / 2 + offY * by
            };
        }

        private static double Or(double v, double d) => v != 0 && !double.IsNaN(v) ? v : d;
    }

    /// <summary>Что встречается в документе — от этого зависит, какие ресурсы нужны рендеру.</summary>
    public sealed class EffectFeatures
    {
        public bool HasTrails, HasLayerGlow, HasFade, HasPostFx, HasGroups, HasDisplace, HasMotionBlur, HasPixelate;

        internal static EffectFeatures Of(EffectDocument doc)
        {
            var f = new EffectFeatures();
            foreach (var l in doc.Layers)
            {
                if (!l.On) continue;
                if (l.HasTrails) f.HasTrails = true;
                if (l.Type != LayerType.PostFx && l.Type != LayerType.Force && l.MayGlow) f.HasLayerGlow = true;
                if (l.FadeOn && l.Type != LayerType.PostFx && l.Type != LayerType.Force) f.HasFade = true;
                if (l.Type == LayerType.PostFx && l.Fx != null)
                {
                    f.HasPostFx = true;
                    if (l.HasTargets) f.HasGroups = true;
                    if (l.Fx.Effect == PostEffect.Displace) f.HasDisplace = true;
                    if (l.Fx.Effect == PostEffect.MotionBlur) f.HasMotionBlur = true;
                    if (l.Fx.Effect == PostEffect.Pixelate) f.HasPixelate = true;
                }
            }
            return f;
        }
    }

    public struct PixelGrid
    {
        public int Gw, Gh;
        public double Bx, By, Ox, Oy;
    }

    public sealed class TextureInfo
    {
        public string Id;
        public string Name;
        public bool HasSheet;
        public int SheetCols = 1, SheetRows = 1;
        public double SheetFps;
    }

    /// <summary>Ссылка на спрайт: процедурная фигура или текстура.</summary>
    public sealed class SpriteRef
    {
        public bool IsTexture;
        public string ShapeId = "soft";
        public string TexId;
        public double? Th, N, Inr, Taper, Var;

        public static SpriteRef Shape(string id) => new SpriteRef { ShapeId = id };

        public static SpriteRef Read(object o, SpriteRef def)
        {
            if (!(o is Dictionary<string, object> d)) return def;
            var r = new SpriteRef();
            string kind = ModelUtil.Str(d, "kind", "shape");
            if (kind == "tex")
            {
                r.IsTexture = true;
                r.TexId = ModelUtil.Str(d, "texId", null);
                return r;
            }
            r.ShapeId = ModelUtil.Str(d, "id", "soft");
            var p = ModelUtil.Obj(d, "p");
            if (p != null)
            {
                r.Th = ModelUtil.NumOpt(p, "th");
                r.N = ModelUtil.NumOpt(p, "n");
                r.Inr = ModelUtil.NumOpt(p, "inr");
                r.Taper = ModelUtil.NumOpt(p, "taper");
                r.Var = ModelUtil.NumOpt(p, "var");
            }
            return r;
        }

        /// <summary>Число вариантов фигуры (<c>Sprites.variants</c>).</summary>
        public int Variants => IsTexture ? 1 : ShapeLibraryInfo.VariantsOf(ShapeId);
    }

    /// <summary>Сведения о процедурных фигурах, нужные симуляции (без рендера).</summary>
    public static class ShapeLibraryInfo
    {
        public static int VariantsOf(string id)
        {
            switch (id)
            {
                case "flame":
                case "smoke":
                case "blob":
                case "shard":
                    return 4;
                default:
                    return 1;
            }
        }
    }

    public sealed class Layer
    {
        public string Id;
        public string Name;
        public LayerType Type;
        public bool On = true, Solo;
        public double Start, End;
        public LayerBlend Blend;
        public Track Opacity;
        public Track GlowL;
        public double GlowLR = 24;
        public bool FadeOn;
        public Track FadeX, FadeY, FadeR;
        public double FadeSoft = 0.5;
        public string[] Targets = Array.Empty<string>();

        public EmitterSettings Em;
        public ParticleSettings Pt;
        public SpriteSettings Sp;
        public PostFxSettings Fx;
        public ForceField[] Fields = Array.Empty<ForceField>();

        internal static Layer Read(Dictionary<string, object> l, EffectDocument doc, bool legacyStep)
        {
            var r = new Layer();
            string type = ModelUtil.Str(l, "type", null);
            switch (type)
            {
                case "emitter": r.Type = LayerType.Emitter; break;
                case "sprite": r.Type = LayerType.Sprite; break;
                case "atlas": r.Type = LayerType.Atlas; break;
                case "postfx": r.Type = LayerType.PostFx; break;
                case "force": r.Type = LayerType.Force; break;
                default: r.Type = l.ContainsKey("sp") && l["sp"] != null ? LayerType.Sprite : LayerType.Emitter; break;
            }
            r.Id = ModelUtil.Str(l, "id", Guid.NewGuid().ToString("N"));
            r.Name = ModelUtil.Str(l, "name", r.Type.ToString());
            r.On = ModelUtil.Bool(l, "on", true);
            r.Solo = ModelUtil.Bool(l, "solo", false);
            r.Start = ModelUtil.Num(l, "start", 0);
            r.End = ModelUtil.Num(l, "end", doc.Duration);
            string defBlend = r.Type == LayerType.Emitter || r.Type == LayerType.Sprite ? "add" : "normal";
            switch (ModelUtil.Str(l, "blend", defBlend))
            {
                case "add": r.Blend = LayerBlend.Add; break;
                case "screen": r.Blend = LayerBlend.Screen; break;
                case "multiply": r.Blend = LayerBlend.Multiply; break;
                default: r.Blend = LayerBlend.Normal; break;
            }
            r.Opacity = Track.Parse(ModelUtil.Get(l, "opacity"), 1);
            r.GlowL = Track.Parse(ModelUtil.Get(l, "glowL"), 0);
            r.GlowLR = ModelUtil.Num(l, "glowLR", 24);
            r.FadeOn = ModelUtil.Bool(l, "fadeOn", false);
            r.FadeX = Track.Parse(ModelUtil.Get(l, "fadeX"), 0);
            r.FadeY = Track.Parse(ModelUtil.Get(l, "fadeY"), 0);
            r.FadeR = Track.Parse(ModelUtil.Get(l, "fadeR"), 220);
            r.FadeSoft = ModelUtil.Num(l, "fadeSoft", 0.5);

            var tg = new List<string>();
            foreach (var t in ModelUtil.Arr(l, "targets")) if (t is string s) tg.Add(s);
            r.Targets = tg.ToArray();

            switch (r.Type)
            {
                case LayerType.Emitter:
                    r.Em = EmitterSettings.Read(ModelUtil.Obj(l, "em"), legacyStep);
                    r.Pt = ParticleSettings.Read(ModelUtil.Obj(l, "pt"), ParticleSettings.Defaults());
                    break;
                case LayerType.Sprite:
                case LayerType.Atlas:
                    r.Sp = SpriteSettings.Read(ModelUtil.Obj(l, "sp"), r.Type == LayerType.Atlas);
                    break;
                case LayerType.PostFx:
                    r.Fx = PostFxSettings.Read(ModelUtil.Obj(l, "fx"));
                    break;
                case LayerType.Force:
                    var ff = ModelUtil.Obj(l, "ff");
                    if (ff == null || !(ModelUtil.Get(ff, "fields") is List<object>))
                    {
                        r.Fields = new[] { ForceField.Read(null) };
                    }
                    else
                    {
                        var list = new List<ForceField>();
                        foreach (var f in ModelUtil.Arr(ff, "fields")) list.Add(ForceField.Read(f as Dictionary<string, object>));
                        r.Fields = list.ToArray();
                    }
                    break;
            }
            return r;
        }

        public bool HasTargets => Targets.Length > 0;

        /// <summary>У слоя есть трейлы (своя частица или частица суб-эмиттера).</summary>
        public bool HasTrails => Type == LayerType.Emitter && (Pt.Trail || (Em.Sub.On && Em.Sub.Pt.Trail));

        /// <summary>Свечение слоя может быть включено хоть в какой-то момент.</summary>
        public bool MayGlow => GlowL.MaxValue() > 0.01;

        /// <summary>
        /// Слой можно рисовать прямо в сцену, без холста композиции: он сам по себе ничего не
        /// требует от оффскрина (нет свечения слоя, затухания, наложения screen/multiply).
        /// Трейлы — только если разрешены простые полосы без трафарета.
        /// </summary>
        public bool CanDrawDirect(bool trailsWithoutStencil)
        {
            switch (Type)
            {
                case LayerType.Force:
                    return true;
                case LayerType.PostFx:
                    return false;
            }
            if (Blend != LayerBlend.Normal && Blend != LayerBlend.Add) return false;
            if (FadeOn || MayGlow) return false;
            if (HasTrails && !trailsWithoutStencil) return false;
            return true;
        }
    }

    public enum EmitterShape { Point, Circle, Ring, Box, Line }
    public enum EmitDir { Out, In, Omni, Dir }

    public struct Burst
    {
        public double T;
        public double N;
    }

    public sealed class EmitterSettings
    {
        public Track X, Y, Sx, Sy, Angle, Spread, Speed, Rate;
        public EmitterShape Shape;
        public EmitDir Dir;
        public double SpeedRnd = 0.5;
        public bool SubStep = true;
        public Burst[] Bursts;
        public double Seed = 1;

        public double BranchChance, BranchSpread = 35, BranchLifeScale = 0.6, BranchSizeScale = 0.7, BranchSpeedScale = 1, BranchMaxGen = 2;

        public PathSettings Path;
        public SubEmitterSettings Sub;

        internal static EmitterSettings Read(Dictionary<string, object> d, bool legacyStep)
        {
            var e = new EmitterSettings
            {
                X = Track.Parse(ModelUtil.Get(d, "x"), 0),
                Y = Track.Parse(ModelUtil.Get(d, "y"), 0),
                Sx = Track.Parse(ModelUtil.Get(d, "sx"), 60),
                Sy = Track.Parse(ModelUtil.Get(d, "sy"), 60),
                Angle = Track.Parse(ModelUtil.Get(d, "angle"), -90),
                Spread = Track.Parse(ModelUtil.Get(d, "spread"), 40),
                Speed = Track.Parse(ModelUtil.Get(d, "speed"), 300),
                Rate = Track.Parse(ModelUtil.Get(d, "rate"), 0),
                SpeedRnd = ModelUtil.Num(d, "speedRnd", 0.5),
                Seed = ModelUtil.Num(d, "seed", 1)
            };
            switch (ModelUtil.Str(d, "shape", "point"))
            {
                case "circle": e.Shape = EmitterShape.Circle; break;
                case "ring": e.Shape = EmitterShape.Ring; break;
                case "box": e.Shape = EmitterShape.Box; break;
                case "line": e.Shape = EmitterShape.Line; break;
                default: e.Shape = EmitterShape.Point; break;
            }
            switch (ModelUtil.Str(d, "dir", "out"))
            {
                case "in": e.Dir = EmitDir.In; break;
                case "omni": e.Dir = EmitDir.Omni; break;
                case "dir": e.Dir = EmitDir.Dir; break;
                default: e.Dir = EmitDir.Out; break;
            }
            // migrate: файлы v<2 эмитят пачкой в начале шага; hydrate для новых — true
            e.SubStep = ModelUtil.Bool(d, "subStep", !(legacyStep && d != null));

            var bursts = new List<Burst>();
            object bo = ModelUtil.Get(d, "bursts");
            if (bo == null) bursts.Add(new Burst { T = 0, N = 24 });
            else
                foreach (var b in ModelUtil.Arr(d, "bursts"))
                    if (b is Dictionary<string, object> bd)
                        bursts.Add(new Burst { T = ModelUtil.Num(bd, "t", double.NaN), N = ModelUtil.Num(bd, "n", double.NaN) });
            // стабильная сортировка по t, как Array.prototype.sort в V8
            var idx = new List<KeyValuePair<int, Burst>>();
            for (int i = 0; i < bursts.Count; i++) idx.Add(new KeyValuePair<int, Burst>(i, bursts[i]));
            idx.Sort((a, b) =>
            {
                double ta = a.Value.T, tb = b.Value.T;
                int c = double.IsNaN(ta) || double.IsNaN(tb) ? 0 : ta.CompareTo(tb);
                return c != 0 ? c : a.Key.CompareTo(b.Key);
            });
            e.Bursts = new Burst[idx.Count];
            for (int i = 0; i < idx.Count; i++) e.Bursts[i] = idx[i].Value;

            var br = ModelUtil.Obj(d, "branch");
            e.BranchChance = ModelUtil.Num(br, "chance", 0);
            e.BranchSpread = ModelUtil.Num(br, "spread", 35);
            e.BranchLifeScale = ModelUtil.Num(br, "lifeScale", 0.6);
            e.BranchSizeScale = ModelUtil.Num(br, "sizeScale", 0.7);
            e.BranchSpeedScale = ModelUtil.Num(br, "speedScale", 1);
            e.BranchMaxGen = ModelUtil.Num(br, "maxGen", 2);

            e.Path = PathSettings.Read(ModelUtil.Obj(d, "path"));
            e.Sub = SubEmitterSettings.Read(ModelUtil.Obj(d, "sub"));
            return e;
        }
    }

    public enum PathMode { Emit, Guide, Both }
    public enum PathAlong { Random, Even, Start }
    public enum PathAim { None, Tangent, Normal }

    public sealed class PathSettings
    {
        public bool On;
        public PathMode Mode;
        public bool Closed;
        public bool Smooth = true;
        public Track[] NodeX, NodeY;
        public PathAlong Along;
        public Track Jitter, Attract, Lock, Flow;
        public PathAim Aim;
        public bool Fast = true;

        public bool Active => On && NodeX.Length >= 1;

        internal static PathSettings Read(Dictionary<string, object> d)
        {
            var p = new PathSettings
            {
                On = ModelUtil.Bool(d, "on", false),
                Closed = ModelUtil.Bool(d, "closed", false),
                Smooth = !(ModelUtil.Get(d, "smooth") is bool b && !b),
                Fast = !(ModelUtil.Get(d, "fast") is bool f && !f),
                Jitter = Track.Parse(ModelUtil.Get(d, "jitter"), 0),
                Attract = Track.Parse(ModelUtil.Get(d, "attract"), 0),
                Lock = Track.Parse(ModelUtil.Get(d, "lock"), 0),
                Flow = Track.Parse(ModelUtil.Get(d, "flow"), 0)
            };
            switch (ModelUtil.Str(d, "mode", "emit"))
            {
                case "guide": p.Mode = PathMode.Guide; break;
                case "both": p.Mode = PathMode.Both; break;
                default: p.Mode = PathMode.Emit; break;
            }
            switch (ModelUtil.Str(d, "along", "random"))
            {
                case "even": p.Along = PathAlong.Even; break;
                case "start": p.Along = PathAlong.Start; break;
                default: p.Along = PathAlong.Random; break;
            }
            switch (ModelUtil.Str(d, "aim", "none"))
            {
                case "tangent": p.Aim = PathAim.Tangent; break;
                case "normal": p.Aim = PathAim.Normal; break;
                default: p.Aim = PathAim.None; break;
            }
            var xs = new List<Track>();
            var ys = new List<Track>();
            object no = ModelUtil.Get(d, "nodes");
            if (no == null)
            {
                xs.Add(new Track(-140)); ys.Add(Track.Zero);
                xs.Add(new Track(140)); ys.Add(Track.Zero);
            }
            else
            {
                foreach (var n in ModelUtil.Arr(d, "nodes"))
                {
                    var nd = n as Dictionary<string, object>;
                    xs.Add(Track.Parse(ModelUtil.Get(nd, "x"), 0));
                    ys.Add(Track.Parse(ModelUtil.Get(nd, "y"), 0));
                }
            }
            p.NodeX = xs.ToArray();
            p.NodeY = ys.ToArray();
            return p;
        }
    }

    public enum SubEmit { Death, Life, Both }
    public enum SubDir { Omni, Inherit, Back, Out }

    public sealed class SubEmitterSettings
    {
        public bool On;
        public SubEmit Emit;
        public Track Rate, Speed;
        public double Count = 8, StartAt, Spread = 60, SpeedRnd = 0.6, Inherit = 0.25, MaxGen = 1, GenScale = 0.6, Budget = 1500;
        public SubDir Dir;
        public bool Under;
        public ParticleSettings Pt;

        internal static SubEmitterSettings Read(Dictionary<string, object> d)
        {
            var s = new SubEmitterSettings
            {
                On = ModelUtil.Bool(d, "on", false),
                Rate = Track.Parse(ModelUtil.Get(d, "rate"), 20),
                Speed = Track.Parse(ModelUtil.Get(d, "speed"), 90),
                Count = ModelUtil.Num(d, "count", 8),
                StartAt = ModelUtil.Num(d, "startAt", 0),
                Spread = ModelUtil.Num(d, "spread", 60),
                SpeedRnd = ModelUtil.Num(d, "speedRnd", 0.6),
                Inherit = ModelUtil.Num(d, "inherit", 0.25),
                MaxGen = ModelUtil.Num(d, "maxGen", 1),
                GenScale = ModelUtil.Num(d, "genScale", 0.6),
                Budget = ModelUtil.Num(d, "budget", 1500),
                Under = ModelUtil.Bool(d, "under", false)
            };
            switch (ModelUtil.Str(d, "emit", "death"))
            {
                case "life": s.Emit = SubEmit.Life; break;
                case "both": s.Emit = SubEmit.Both; break;
                default: s.Emit = SubEmit.Death; break;
            }
            switch (ModelUtil.Str(d, "dir", "omni"))
            {
                case "inherit": s.Dir = SubDir.Inherit; break;
                case "back": s.Dir = SubDir.Back; break;
                case "out": s.Dir = SubDir.Out; break;
                default: s.Dir = SubDir.Omni; break;
            }
            s.Pt = ParticleSettings.Read(ModelUtil.Obj(d, "pt"), ParticleSettings.SubDefaults());
            return s;
        }
    }

    public enum TurbMode { Noise, Curl }

    /// <summary>Блок частицы (<c>layer.pt</c> и <c>em.sub.pt</c>).</summary>
    public sealed class ParticleSettings
    {
        public double Life = 0.8, LifeRnd = 0.3;
        public bool Trail;
        public SpriteRef Sprite = SpriteRef.Shape("smoke");
        public double TrailCore;
        public Track Size;
        public double SizeRnd = 0.4;
        public Curve SizeOL, SizeOT, OpacityOL;
        public ColorRamp Grad;
        public bool TintTex;
        public double Rot, RotRnd = 1, Spin = 40, SpinRnd = 1;
        public bool AlignVel;
        public double Stretch;
        public Track GravX, GravY, TurbAmp;
        public double Drag = 2, TurbFreq = 1.5, Zigzag;
        public TurbMode TurbMode;
        public double TurbScale = 90, TurbOct = 2, TurbRise, TurbWind, TurbSeed, TurbRamp;
        public double Squash, Glow, GlowSize = 1.6, GlowBlur = 0.15, Fps;

        internal static ParticleSettings Defaults()
        {
            return new ParticleSettings
            {
                Size = new Track(90),
                SizeOL = Curve.Make(0, 0.25, 0.5, 0.8, 1, 1),
                SizeOT = Curve.Const1,
                OpacityOL = Curve.Make(0, 1, 0.55, 0.85, 1, 0),
                Grad = ColorRamp.Make(0, "#fff8e0", 0.2, "#ffd257", 0.45, "#ff8a2a", 0.7, "#c93a12", 1, "#3a2018"),
                GravX = Track.Zero, GravY = Track.Zero, TurbAmp = Track.Zero
            };
        }

        internal static ParticleSettings SubDefaults()
        {
            var p = Defaults();
            p.Sprite = SpriteRef.Shape("dot");
            p.Life = 0.35; p.LifeRnd = 0.4;
            p.Size = new Track(18); p.SizeRnd = 0.5;
            p.SizeOL = Curve.Make(0, 1, 0.6, 0.7, 1, 0.1);
            p.OpacityOL = Curve.Make(0, 1, 0.55, 0.85, 1, 0);
            p.Grad = ColorRamp.Make(0, "#ffffff", 0.3, "#ffe37a", 0.7, "#ff9030", 1, "#b03010");
            p.Drag = 1.5; p.Spin = 0; p.SpinRnd = 0;
            return p;
        }

        internal static ParticleSettings Read(Dictionary<string, object> d, ParticleSettings def)
        {
            if (d == null) return def;
            return new ParticleSettings
            {
                Life = ModelUtil.Num(d, "life", def.Life),
                LifeRnd = ModelUtil.Num(d, "lifeRnd", def.LifeRnd),
                Trail = ModelUtil.Str(d, "render", "sprite") == "trail",
                Sprite = SpriteRef.Read(ModelUtil.Get(d, "sprite"), def.Sprite),
                TrailCore = ModelUtil.Num(d, "trailCore", 0),
                Size = ModelUtil.Get(d, "size") == null ? def.Size : Track.Parse(ModelUtil.Get(d, "size"), 0),
                SizeRnd = ModelUtil.Num(d, "sizeRnd", def.SizeRnd),
                SizeOL = Curve.Parse(ModelUtil.Get(d, "sizeOL"), def.SizeOL),
                SizeOT = Curve.Parse(ModelUtil.Get(d, "sizeOT"), def.SizeOT),
                OpacityOL = Curve.Parse(ModelUtil.Get(d, "opacityOL"), def.OpacityOL),
                Grad = ColorRamp.Parse(ModelUtil.Get(d, "grad"), def.Grad),
                TintTex = ModelUtil.Bool(d, "tintTex", false),
                Rot = ModelUtil.Num(d, "rot", def.Rot),
                RotRnd = ModelUtil.Num(d, "rotRnd", def.RotRnd),
                Spin = ModelUtil.Num(d, "spin", def.Spin),
                SpinRnd = ModelUtil.Num(d, "spinRnd", def.SpinRnd),
                AlignVel = ModelUtil.Bool(d, "alignVel", false),
                Stretch = ModelUtil.Num(d, "stretch", 0),
                GravX = Track.Parse(ModelUtil.Get(d, "gravX"), 0),
                GravY = Track.Parse(ModelUtil.Get(d, "gravY"), 0),
                Drag = ModelUtil.Num(d, "drag", def.Drag),
                TurbAmp = Track.Parse(ModelUtil.Get(d, "turbAmp"), 0),
                TurbFreq = ModelUtil.Num(d, "turbFreq", 1.5),
                Zigzag = ModelUtil.Num(d, "zigzag", 0),
                TurbMode = ModelUtil.Str(d, "turbMode", "noise") == "curl" ? TurbMode.Curl : TurbMode.Noise,
                TurbScale = ModelUtil.Num(d, "turbScale", 90),
                TurbOct = ModelUtil.Num(d, "turbOct", 2),
                TurbRise = ModelUtil.Num(d, "turbRise", 0),
                TurbWind = ModelUtil.Num(d, "turbWind", 0),
                TurbSeed = ModelUtil.Num(d, "turbSeed", 0),
                TurbRamp = ModelUtil.Num(d, "turbRamp", 0),
                Squash = ModelUtil.Num(d, "squash", 0),
                Glow = ModelUtil.Num(d, "glow", 0),
                GlowSize = ModelUtil.Num(d, "glowSize", 1.6),
                GlowBlur = ModelUtil.Num(d, "glowBlur", 0.15),
                Fps = ModelUtil.Num(d, "fps", 0)
            };
        }
    }

    /// <summary>Блок спрайт-слоя и атлас-слоя (<c>layer.sp</c>).</summary>
    public sealed class SpriteSettings
    {
        public SpriteRef Sprite;
        public double Size = 220;
        public Track X, Y, Scale, Rot, Color, Glow;
        public double Aspect = 1, Squash, GlowSize = 1.6, GlowBlur = 0.15, Fps;

        internal static SpriteSettings Read(Dictionary<string, object> d, bool atlas)
        {
            return new SpriteSettings
            {
                Sprite = SpriteRef.Read(ModelUtil.Get(d, "sprite"), atlas ? new SpriteRef { IsTexture = true } : SpriteRef.Shape("soft")),
                Size = ModelUtil.Num(d, "size", atlas ? 256 : 220),
                X = Track.Parse(ModelUtil.Get(d, "x"), 0),
                Y = Track.Parse(ModelUtil.Get(d, "y"), 0),
                Scale = Track.Parse(ModelUtil.Get(d, "scale"), 1),
                Rot = Track.Parse(ModelUtil.Get(d, "rot"), 0),
                Color = Track.ParseColor(ModelUtil.Get(d, "color"), 255, 255, 255),
                Glow = Track.Parse(ModelUtil.Get(d, "glow"), 0),
                Aspect = ModelUtil.Num(d, "aspect", 1),
                Squash = ModelUtil.Num(d, "squash", 0),
                GlowSize = ModelUtil.Num(d, "glowSize", 1.6),
                GlowBlur = ModelUtil.Num(d, "glowBlur", 0.15),
                Fps = ModelUtil.Num(d, "fps", 0)
            };
        }
    }

    public enum PostEffect { None, MotionBlur, Displace, Pixelate }

    public sealed class PostFxSettings
    {
        public PostEffect Effect;
        public bool Radial;
        public Track Length, Angle, Spin, X, Y, Amount, Y0, PxThr;
        public double Samples = 16;
        public bool Half;
        // displace
        public double Scale = 60, Aspect = 2.4, Oct = 3, Evo = 0.6, Rise = 150, Ramp, Soft = 120, Seed;
        public bool Swirl = true;
        // pixelate
        public double PxW = 64, PxAspect = 1, PxOffX, PxOffY, PxGain = 1.5, PxALev = 4, PxLev = 6, PxRampN = 6, PxSat = 1.15, PxCon = 1, PxDither, PxDitherAmt = 0.7, PxOut;
        public bool PxSnap = true;
        public string PxSample = "peak", PxAlpha = "cut", PxColor = "none", PxPal = "pico8", PxOutMode = "outer";
        public ColorRamp PxRamp;
        public double[] PxOutCol = { 10, 8, 16 };

        internal static PostFxSettings Read(Dictionary<string, object> d)
        {
            var f = new PostFxSettings();
            switch (ModelUtil.Str(d, "effect", "mblur"))
            {
                case "mblur": f.Effect = PostEffect.MotionBlur; break;
                case "displace": f.Effect = PostEffect.Displace; break;
                case "pixelate": f.Effect = PostEffect.Pixelate; break;
                default: f.Effect = PostEffect.None; break;
            }
            f.Radial = ModelUtil.Str(d, "mode", "directional") == "radial";
            f.Length = Track.Parse(ModelUtil.Get(d, "length"), 24);
            f.Angle = Track.Parse(ModelUtil.Get(d, "angle"), 0);
            f.Spin = Track.Parse(ModelUtil.Get(d, "spin"), 0);
            f.X = Track.Parse(ModelUtil.Get(d, "x"), 0);
            f.Y = Track.Parse(ModelUtil.Get(d, "y"), 0);
            f.Samples = ModelUtil.Num(d, "samples", 16);
            f.Half = ModelUtil.Bool(d, "half", false);

            f.Amount = Track.Parse(ModelUtil.Get(d, "amount"), 26);
            f.Scale = ModelUtil.Num(d, "scale", 60);
            f.Aspect = ModelUtil.Num(d, "aspect", 2.4);
            f.Swirl = ModelUtil.Str(d, "field", "swirl") == "swirl";
            f.Oct = ModelUtil.Num(d, "oct", 3);
            f.Evo = ModelUtil.Num(d, "evo", 0.6);
            f.Rise = ModelUtil.Num(d, "rise", 150);
            f.Ramp = ModelUtil.Num(d, "ramp", 0);
            f.Y0 = Track.Parse(ModelUtil.Get(d, "y0"), 0);
            f.Soft = ModelUtil.Num(d, "soft", 120);
            f.Seed = ModelUtil.Num(d, "seed", 0);

            f.PxW = ModelUtil.Num(d, "pxW", 64);
            f.PxSnap = !(ModelUtil.Get(d, "pxSnap") is bool ps && !ps);
            f.PxAspect = ModelUtil.Num(d, "pxAspect", 1);
            f.PxOffX = ModelUtil.Num(d, "pxOffX", 0);
            f.PxOffY = ModelUtil.Num(d, "pxOffY", 0);
            f.PxSample = ModelUtil.Str(d, "pxSample", "peak");
            f.PxGain = ModelUtil.Num(d, "pxGain", 1.5);
            f.PxAlpha = ModelUtil.Str(d, "pxAlpha", "cut");
            f.PxThr = Track.Parse(ModelUtil.Get(d, "pxThr"), 0.45);
            f.PxALev = ModelUtil.Num(d, "pxALev", 4);
            f.PxColor = ModelUtil.Str(d, "pxColor", "none");
            f.PxLev = ModelUtil.Num(d, "pxLev", 6);
            f.PxPal = ModelUtil.Str(d, "pxPal", "pico8");
            f.PxRamp = ColorRamp.Parse(ModelUtil.Get(d, "pxRamp"),
                ColorRamp.Make(0, "#fff8e0", 0.2, "#ffd257", 0.45, "#ff8a2a", 0.7, "#c93a12", 1, "#3a2018"));
            f.PxRampN = ModelUtil.Num(d, "pxRampN", 6);
            f.PxSat = ModelUtil.Num(d, "pxSat", 1.15);
            f.PxCon = ModelUtil.Num(d, "pxCon", 1);
            f.PxDither = ModelUtil.Num(d, "pxDither", 0);
            f.PxDitherAmt = ModelUtil.Num(d, "pxDitherAmt", 0.7);
            f.PxOut = ModelUtil.Num(d, "pxOut", 0);
            if (ModelUtil.Get(d, "pxOutCol") is List<object> oc && oc.Count >= 3)
                f.PxOutCol = new[] { ModelUtil.ToNum(oc[0]), ModelUtil.ToNum(oc[1]), ModelUtil.ToNum(oc[2]) };
            f.PxOutMode = ModelUtil.Str(d, "pxOutMode", "outer");
            return f;
        }
    }

    public enum ForceMode { Collapse, Expand, Drive }

    public sealed class ForceField
    {
        public bool On = true;
        public ForceMode Mode;
        public Track Strength, X, Y, Sx, Sy;
        public double Falloff = 1;

        internal static ForceField Read(Dictionary<string, object> d)
        {
            var f = new ForceField
            {
                // Model.fillForceField дозаполняет null; движок пропускает только on === false
                On = !(ModelUtil.Get(d, "on") is bool on && !on),
                Strength = Track.Parse(ModelUtil.Get(d, "strength"), 600),
                X = Track.Parse(ModelUtil.Get(d, "x"), 0),
                Y = Track.Parse(ModelUtil.Get(d, "y"), 0),
                Sx = Track.Parse(ModelUtil.Get(d, "sx"), 200),
                Sy = Track.Parse(ModelUtil.Get(d, "sy"), 200),
                Falloff = ModelUtil.Num(d, "falloff", 1)
            };
            switch (ModelUtil.Str(d, "mode", "collapse"))
            {
                case "expand": f.Mode = ForceMode.Expand; break;
                case "drive": f.Mode = ForceMode.Drive; break;
                default: f.Mode = ForceMode.Collapse; break;
            }
            return f;
        }
    }

    /// <summary>Палитры пиксель-арта (<c>Model.PIXEL_PALETTES</c>).</summary>
    public static class PixelPalettes
    {
        private static readonly Dictionary<string, string[]> Map = new Dictionary<string, string[]>
        {
            { "mono", new[] { "#000000", "#ffffff" } },
            { "gb", new[] { "#0f380f", "#306230", "#8bac0f", "#9bbc0f" } },
            { "cga", new[] {
                "#000000", "#0000aa", "#00aa00", "#00aaaa", "#aa0000", "#aa00aa", "#aa5500", "#aaaaaa",
                "#555555", "#5555ff", "#55ff55", "#55ffff", "#ff5555", "#ff55ff", "#ffff55", "#ffffff" } },
            { "pico8", new[] {
                "#000000", "#1d2b53", "#7e2553", "#008751", "#ab5236", "#5f574f", "#c2c3c7", "#fff1e8",
                "#ff004d", "#ffa300", "#ffec27", "#00e436", "#29adff", "#83769c", "#ff77a8", "#ffccaa" } },
            { "sweetie16", new[] {
                "#1a1c2c", "#5d275d", "#b13e53", "#ef7d57", "#ffcd75", "#a7f070", "#38b764", "#257179",
                "#29366f", "#3b5dc9", "#41a6f6", "#73eff7", "#f4f4f4", "#94b0c2", "#566c86", "#333c57" } },
            { "nes", new[] {
                "#7c7c7c", "#0000fc", "#0000bc", "#4428bc", "#940084", "#a80020", "#a81000", "#881400",
                "#503000", "#007800", "#006800", "#005800", "#004058",
                "#bcbcbc", "#0078f8", "#0058f8", "#6844fc", "#d800cc", "#e40058", "#f83800", "#e45c10",
                "#ac7c00", "#00b800", "#00a800", "#00a844", "#008888",
                "#f8f8f8", "#3cbcfc", "#6888fc", "#9878f8", "#f878f8", "#f85898", "#f87858", "#fca044",
                "#f8b800", "#b8f818", "#58d854", "#58f898", "#00e8d8", "#787878",
                "#fcfcfc", "#a4e4fc", "#b8b8f8", "#d8b8f8", "#f8b8f8", "#f8a4c0", "#f0d0b0", "#fce0a8",
                "#f8d878", "#d8f878", "#b8f8b8", "#b8f8d8", "#00fcfc", "#000000" } },
            { "ember", new[] {
                "#0d0503", "#1a0a06", "#3b1208", "#66210c", "#99380f", "#c85418",
                "#e87a1e", "#f5a52c", "#ffc851", "#ffe58a", "#fff6cf", "#ffffff" } },
            { "ice", new[] {
                "#030509", "#050a14", "#0b1a33", "#123055", "#1a4c7d", "#2470a8",
                "#3897cc", "#59bde4", "#86d9f0", "#b3ecf7", "#d9f6fb", "#ffffff" } }
        };

        /// <summary>Цвета палитры (неизвестный id — pico8, как в редакторе).</summary>
        public static string[] Get(string id) => id != null && Map.TryGetValue(id, out var c) ? c : Map["pico8"];
    }

    /// <summary>Доступ к дереву JSON с JS-семантикой («пусто» = дефолт).</summary>
    public static class ModelUtil
    {
        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            return d.TryGetValue(key, out object v) ? v : null;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key) => Get(d, key) as Dictionary<string, object>;

        public static List<object> Arr(Dictionary<string, object> d, string key) => Get(d, key) as List<object> ?? new List<object>();

        /// <summary>Число; null/отсутствие — дефолт (так ведёт себя hydrate).</summary>
        public static double Num(Dictionary<string, object> d, string key, double def)
        {
            object v = Get(d, key);
            if (v == null) return def;
            return ToNum(v, def);
        }

        public static double? NumOpt(Dictionary<string, object> d, string key)
        {
            object v = Get(d, key);
            if (v == null) return null;
            return ToNum(v);
        }

        public static double ToNum(object v, double def = 0)
        {
            switch (v)
            {
                case double d: return d;
                case bool b: return b ? 1 : 0;
                case string s:
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : double.NaN;
                case Dictionary<string, object> o:
                    // трек в статичном поле — берём значение в момент 0
                    return Track.Parse(o, def).Eval(0);
                default: return def;
            }
        }

        public static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v = Get(d, key);
            return v is string s ? s : def;
        }

        /// <summary>JS-истинность; отсутствие — дефолт.</summary>
        public static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            object v = Get(d, key);
            if (v == null) return def;
            switch (v)
            {
                case bool b: return b;
                case double n: return n != 0 && !double.IsNaN(n);
                case string s: return s.Length > 0;
                default: return true;
            }
        }

        public static void HexToRgb(string hex, out double r, out double g, out double b)
        {
            string h = (hex ?? "#ffffff").Replace("#", "");
            if (h.Length == 3) h = new string(new[] { h[0], h[0], h[1], h[1], h[2], h[2] });
            int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int n);
            r = (n >> 16) & 255;
            g = (n >> 8) & 255;
            b = n & 255;
        }

        /// <summary>Канал цвета 0..255 -> байт, как <c>rgbToHex</c>.</summary>
        public static byte ToByte(double v)
        {
            if (double.IsNaN(v)) return 0;
            return (byte)Js.Round(Js.Clamp(v, 0, 255));
        }
    }
}
