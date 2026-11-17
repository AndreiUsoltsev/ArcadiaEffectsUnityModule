using System;
using System.Collections.Generic;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Частица эмиттера (поля как в js/core/sim.js).</summary>
    public sealed class Particle
    {
        public double X, Y, Vx, Vy, Age, Life, Size0, Rot, Spin, Seed;
        public int Vi, Gen, StepN, Tier, PSeg;
        public double SubAcc;
        internal bool Dead;
        internal bool HasHist;
        private readonly List<double> _hist = new List<double>(16);

        /// <summary>История пути трейла: x,y подряд; null — частица не трейл.</summary>
        public List<double> Hist => HasHist ? _hist : null;

        internal void StartHist(double x, double y)
        {
            _hist.Clear();
            _hist.Add(x);
            _hist.Add(y);
            HasHist = true;
        }
    }

    /// <summary>
    /// Детерминированная симуляция одного эмиттера: фиксированный шаг 1/120, сидированный RNG.
    /// Перемотка назад = сброс и пересчёт с нуля (как в редакторе).
    /// </summary>
    public sealed class Simulation
    {
        public const double DT = 1.0 / 120;
        public const int MaxParts = 5000;
        private const double Phi = 0.6180339887498949;
        private static readonly double FlowK = Math.Min(1, 8 * DT);
        private const int PWin = 4;

        private readonly Layer _layer;
        private readonly List<Layer> _forces;

        public double T { get; private set; }
        public readonly List<Particle> Parts = new List<Particle>();

        private Js.Mulberry32 _rng;
        private readonly int _maxParts;
        private int _live;
        private readonly Stack<Particle> _free = new Stack<Particle>();
        private double _acc;
        private int _bi;
        private int _subN;
        private readonly PathBake _pbuf = new PathBake();
        private readonly PathBake _pbuf2 = new PathBake();
        private PathBake _pe, _pe2;
        private double _pjit;
        private int _pathIdx;
        private readonly List<double> _fbuf = new List<double>();

        /// <param name="maxParticles">Кап живых частиц (в редакторе 5000; меньше — только на слабых уровнях качества).</param>
        public Simulation(Layer layer, List<Layer> forces, int maxParticles = MaxParts)
        {
            _layer = layer;
            _forces = forces;
            _maxParts = Math.Max(1, maxParticles);
            Reset();
        }

        private Particle Rent()
        {
            Particle p = _free.Count > 0 ? _free.Pop() : new Particle();
            p.Dead = false;
            p.HasHist = false;
            _live++;
            return p;
        }

        public void Reset()
        {
            T = 0;
            _rng = new Js.Mulberry32(Js.ToInt32((double)Js.ToInt32(_layer.Em.Seed) * 7919 + 1013));
            _acc = 0;
            _bi = 0;
            for (int i = 0; i < Parts.Count; i++) _free.Push(Parts[i]);
            Parts.Clear();
            _live = 0;
            _subN = 0;
            _pe = null;
            _pe2 = null;
            _pjit = 0;
            _pathIdx = 0;
        }

        /// <summary>
        /// Довести симуляцию до последнего узла сетки &lt;= tl (время слоя).
        /// Возвращает остаток для экстраполяции отрисовки.
        /// </summary>
        public double Ensure(double tl)
        {
            if (tl < T - 1e-6) Reset();
            int guard = 0;
            while (T + DT <= tl + 1e-9 && guard++ < 4000) Step();
            return Math.Max(0, tl - T);
        }

        private ParticleSettings PtOf(int tier) => tier > 0 ? _layer.Em.Sub.Pt : _layer.Pt;

        // ---------- силовые поля ----------
        private const int FfN = 7;

        private bool ForceStep(double tl)
        {
            _fbuf.Clear();
            if (_forces == null) return false;
            double gt = _layer.Start + tl;
            for (int i = 0; i < _forces.Count; i++)
            {
                Layer fl = _forces[i];
                double flt = gt - fl.Start;
                if (flt < 0 || flt > fl.End - fl.Start) continue;
                double gain = Js.Clamp(fl.Opacity.Eval(flt), 0, 1);
                if (gain <= 0.001) continue;
                ForceField[] fields = fl.Fields;
                for (int j = 0; j < fields.Length; j++)
                {
                    ForceField f = fields[j];
                    if (!f.On) continue;
                    double s = f.Strength.Eval(flt) * gain;
                    if (s == 0) continue;
                    double sx = Math.Max(1, f.Sx.Eval(flt)), sy = Math.Max(1, f.Sy.Eval(flt));
                    _fbuf.Add(f.Mode == ForceMode.Expand ? 1 : (f.Mode == ForceMode.Drive ? 2 : 0));
                    _fbuf.Add(f.X.Eval(flt));
                    _fbuf.Add(f.Y.Eval(flt));
                    _fbuf.Add(sx);
                    _fbuf.Add(sy);
                    _fbuf.Add(s);
                    _fbuf.Add(Math.Max(0, f.Falloff));
                }
            }
            return _fbuf.Count > 0;
        }

        private struct Phys
        {
            public double Gx, Gy, Ta, Tf, Tsc, Toc, Tri, Tsd, Tw, Tramp, DragF, Zig;
            public bool Curl, Trail;
        }

        private static Phys PhysOf(ParticleSettings pt, double tl)
        {
            bool curl = pt.TurbMode == TurbMode.Curl;
            int toc = Js.ToInt32(pt.TurbOct);
            return new Phys
            {
                Gx = pt.GravX.Eval(tl), Gy = pt.GravY.Eval(tl),
                Ta = pt.TurbAmp.Eval(tl), Tf = pt.TurbFreq != 0 && !double.IsNaN(pt.TurbFreq) ? pt.TurbFreq : 1,
                Curl = curl,
                Tsc = curl ? Math.Max(4, pt.TurbScale != 0 ? pt.TurbScale : 90) : 0,
                Toc = curl ? Js.Clamp(toc != 0 ? toc : 1, 1, 4) : 1,
                Tri = curl ? pt.TurbRise : 0,
                Tsd = curl ? (double)Js.ToInt32(pt.TurbSeed) * 4099 : 0,
                Tw = curl ? pt.TurbWind : 0,
                Tramp = Js.Clamp(pt.TurbRamp, 0, 1),
                DragF = pt.Drag > 0 ? Math.Exp(-pt.Drag * DT) : 1,
                Zig = pt.Zigzag * Js.DEG,
                Trail = pt.Trail
            };
        }

        private sealed class SubCtx
        {
            public double Ex, Ey, Speed, Rate, Size, StartAt;
            public int Count, NVar, Budget, MaxGen;
            public bool Trail, EmitLife, EmitDeath;
            public readonly double[] GenScale = new double[5];
        }

        private readonly SubCtx _sc = new SubCtx();

        private SubCtx MakeSubCtx(EmitterSettings em, double tl)
        {
            SubEmitterSettings sub = em.Sub;
            ParticleSettings spt = sub.Pt;
            int mg = Js.ToInt32(sub.MaxGen);
            int maxGen = Math.Max(1, Math.Min(4, mg != 0 ? mg : 1));
            double g = Math.Max(0.05, sub.GenScale);
            SubCtx sc = _sc;
            sc.GenScale[0] = 1;
            sc.GenScale[1] = 1;
            for (int i = 2; i <= 4; i++) sc.GenScale[i] = i <= maxGen ? sc.GenScale[i - 1] * g : 0;
            sc.Ex = em.X.Eval(tl);
            sc.Ey = em.Y.Eval(tl);
            sc.Speed = Math.Max(0, sub.Speed.Eval(tl));
            sc.Rate = Math.Max(0, sub.Rate.Eval(tl));
            sc.Size = Math.Max(1, spt.Size.Eval(tl));
            sc.Count = (int)Math.Max(0, Js.Round(sub.Count));
            sc.StartAt = Js.Clamp(sub.StartAt, 0, 1);
            sc.NVar = spt.Sprite.Variants;
            sc.Trail = spt.Trail;
            sc.Budget = Math.Max(0, Js.ToInt32(sub.Budget));
            sc.MaxGen = maxGen;
            sc.EmitLife = sub.Emit == SubEmit.Life || sub.Emit == SubEmit.Both;
            sc.EmitDeath = sub.Emit == SubEmit.Death || sub.Emit == SubEmit.Both;
            return sc;
        }

        private void Spawn(int n, double tl, double sA0, double sInc)
        {
            EmitterSettings em = _layer.Em;
            ParticleSettings pt = _layer.Pt;
            double ex = em.X.Eval(tl), ey = em.Y.Eval(tl);
            double sx = Math.Max(0, em.Sx.Eval(tl)), sy = Math.Max(0, em.Sy.Eval(tl));
            double angle = em.Angle.Eval(tl) * Js.DEG;
            double spread = em.Spread.Eval(tl) * Js.DEG;
            double baseSpeed = em.Speed.Eval(tl);
            double baseSize = pt.Size.Eval(tl);
            int nVar = pt.Sprite.Variants;
            PathBake pb = _pe, pb2 = _pe2;
            PathAim pAim = pb != null ? em.Path.Aim : PathAim.None;
            PathAlong pAlong = pb != null ? em.Path.Along : PathAlong.Random;
            double jit = _pjit;
            double sgx = sInc > 0 ? pt.GravX.Eval(tl) : 0;
            double sgy = sInc > 0 ? pt.GravY.Eval(tl) : 0;
            double ex2 = sInc > 0 ? em.X.Eval(tl + DT) : ex;
            double ey2 = sInc > 0 ? em.Y.Eval(tl + DT) : ey;

            for (int i = 0; i < n; i++)
            {
                if (_live >= _maxParts) return;
                double f = sInc > 0 ? Js.Clamp((i + 1 - sA0) / sInc, 0, 1) : 0;
                double cex = ex + (ex2 - ex) * f, cey = ey + (ey2 - ey) * f;
                double x = cex, y = cey;
                double ptx = 1, pty = 0, side = 1;
                if (pb != null)
                {
                    double ru = _rng.Next(), rj = _rng.Next() * 2 - 1;
                    double u = pAlong == PathAlong.Even ? (_pathIdx * Phi) % 1 : (pAlong == PathAlong.Start ? 0 : ru);
                    _pathIdx++;
                    PathBake.At q = pb.PointAt(u);
                    double qx = q.X, qy = q.Y;
                    ptx = q.Tx; pty = q.Ty;
                    if (pb2 != null && f > 0)
                    {
                        PathBake.At q2 = pb2.PointAt(u);
                        qx += (q2.X - qx) * f;
                        qy += (q2.Y - qy) * f;
                        double tx = ptx + (q2.Tx - ptx) * f, ty = pty + (q2.Ty - pty) * f;
                        double tn = Math.Sqrt(tx * tx + ty * ty);
                        if (tn > 1e-6) { ptx = tx / tn; pty = ty / tn; }
                    }
                    side = rj < 0 ? -1 : 1;
                    x = qx - pty * rj * jit;
                    y = qy + ptx * rj * jit;
                }
                else if (em.Shape == EmitterShape.Circle)
                {
                    double a = _rng.Next() * Math.PI * 2, d = Math.Sqrt(_rng.Next());
                    x = cex + Math.Cos(a) * d * sx;
                    y = cey + Math.Sin(a) * d * sy;
                }
                else if (em.Shape == EmitterShape.Ring)
                {
                    double a = _rng.Next() * Math.PI * 2, d = sx + (_rng.Next() - 0.5) * sy;
                    x = cex + Math.Cos(a) * d;
                    y = cey + Math.Sin(a) * d;
                }
                else if (em.Shape == EmitterShape.Box)
                {
                    x = cex + (_rng.Next() * 2 - 1) * sx;
                    y = cey + (_rng.Next() * 2 - 1) * sy;
                }
                else if (em.Shape == EmitterShape.Line)
                {
                    double d = (_rng.Next() * 2 - 1) * sx;
                    x = cex + Math.Cos(angle) * d;
                    y = cey + Math.Sin(angle) * d;
                }

                double ang;
                if (pAim == PathAim.Tangent) ang = Math.Atan2(pty, ptx) + (_rng.Next() - 0.5) * spread;
                else if (pAim == PathAim.Normal) ang = Math.Atan2(ptx * side, -pty * side) + (_rng.Next() - 0.5) * spread;
                else if (em.Dir == EmitDir.Dir) ang = angle + (_rng.Next() - 0.5) * spread;
                else if (em.Dir == EmitDir.Omni) ang = _rng.Next() * Math.PI * 2;
                else
                {
                    double dx = x - cex, dy = y - cey;
                    ang = (dx * dx + dy * dy > 1e-6) ? Math.Atan2(dy, dx) : _rng.Next() * Math.PI * 2;
                    if (em.Dir == EmitDir.In) ang += Math.PI;
                }
                double spd = Math.Max(0, baseSpeed * (1 + em.SpeedRnd * (_rng.Next() * 2 - 1)));
                double life = Math.Max(0.05, pt.Life * (1 + pt.LifeRnd * (_rng.Next() * 2 - 1)));
                double size0 = Math.Max(1, baseSize * (1 + pt.SizeRnd * (_rng.Next() * 2 - 1)));
                double rot0 = (pt.Rot + 360 * pt.RotRnd * (_rng.Next() - 0.5)) * Js.DEG;
                double spin = pt.Spin * (1 + pt.SpinRnd * (_rng.Next() * 2 - 1)) * Js.DEG;
                double seed = Math.Floor(_rng.Next() * 1e6);
                int varI = (int)Math.Floor(_rng.Next() * nVar);

                double bvx = Math.Cos(ang) * spd, bvy = Math.Sin(ang) * spd;
                double back = f * DT;

                Particle np = Rent();
                np.X = x - bvx * back; np.Y = y - bvy * back;
                np.Vx = bvx - sgx * back; np.Vy = bvy - sgy * back;
                np.Age = -back; np.Life = life;
                np.Size0 = size0; np.Rot = rot0 - spin * back; np.Spin = spin;
                np.Seed = seed; np.Vi = varI;
                np.Gen = 0; np.StepN = 0;
                np.Tier = 0; np.SubAcc = 0; np.PSeg = -1;
                if (pt.Trail) np.StartHist(x, y);
                Parts.Add(np);
            }
        }

        private void SpawnSub(Particle p, int n, int salt, SubCtx sc)
        {
            SubEmitterSettings sub = _layer.Em.Sub;
            ParticleSettings spt = sub.Pt;
            int tier = p.Tier + 1;
            double gs = tier < sc.GenScale.Length && sc.GenScale[tier] != 0 ? sc.GenScale[tier] : 1;
            for (int i = 0; i < n; i++)
            {
                if (_live >= _maxParts || _subN >= sc.Budget) return;
                var cr = new Js.Mulberry32(Js.ToInt32((double)Js.ToInt32(p.Seed) * 65599 + (double)p.StepN * 40503 + (double)i * 8191 + salt));
                double rA = cr.Next(), rS = cr.Next(), rL = cr.Next(), rZ = cr.Next(), rR = cr.Next(), rP = cr.Next(), rV = cr.Next(), rD = cr.Next();

                double ang;
                if (sub.Dir == SubDir.Omni)
                {
                    ang = rA * Math.PI * 2;
                }
                else
                {
                    double bas;
                    if (sub.Dir == SubDir.Out)
                    {
                        double dx = p.X - sc.Ex, dy = p.Y - sc.Ey;
                        bas = (dx * dx + dy * dy > 1e-6) ? Math.Atan2(dy, dx) : rA * Math.PI * 2;
                    }
                    else
                    {
                        bas = (p.Vx * p.Vx + p.Vy * p.Vy > 1e-6) ? Math.Atan2(p.Vy, p.Vx) : rA * Math.PI * 2;
                        if (sub.Dir == SubDir.Back) bas += Math.PI;
                    }
                    ang = bas + (rA - 0.5) * sub.Spread * Js.DEG;
                }
                double spd = Math.Max(0, sc.Speed * gs * (1 + sub.SpeedRnd * (rS * 2 - 1)));
                double inh = sub.Inherit;

                _subN++;
                Particle np = Rent();
                np.X = p.X; np.Y = p.Y;
                np.Vx = Math.Cos(ang) * spd + p.Vx * inh;
                np.Vy = Math.Sin(ang) * spd + p.Vy * inh;
                np.Age = 0;
                np.Life = Math.Max(0.05, spt.Life * gs * (1 + spt.LifeRnd * (rL * 2 - 1)));
                np.Size0 = Math.Max(1, sc.Size * gs * (1 + spt.SizeRnd * (rZ * 2 - 1)));
                np.Rot = (spt.Rot + 360 * spt.RotRnd * (rR - 0.5)) * Js.DEG;
                np.Spin = spt.Spin * (1 + spt.SpinRnd * (rP * 2 - 1)) * Js.DEG;
                np.Seed = Math.Floor(rD * 1e6); np.Vi = (int)Math.Floor(rV * sc.NVar);
                np.Gen = 0; np.StepN = 0;
                np.Tier = tier; np.SubAcc = 0; np.PSeg = -1;
                if (sc.Trail) np.StartHist(p.X, p.Y);
                Parts.Add(np);
            }
        }

        private void SpawnBranch(Particle p)
        {
            if (_live >= _maxParts) return;
            ParticleSettings pt = PtOf(p.Tier);
            EmitterSettings em = _layer.Em;
            var cr = new Js.Mulberry32(Js.ToInt32((double)Js.ToInt32(p.Seed) * 31 + (double)p.StepN * 10007));
            double ang = Math.Atan2(p.Vy, p.Vx) + (cr.Next() * 2 - 1) * Or(em.BranchSpread, 35) * Js.DEG;
            double spd = Hypot(p.Vx, p.Vy) * Or(em.BranchSpeedScale, 1) * (0.75 + 0.5 * cr.Next());
            double remain = Math.Max(0.05, p.Life - p.Age);
            double life = Math.Max(0.05, remain * Or(em.BranchLifeScale, 0.6) * (0.6 + 0.8 * cr.Next()));
            double nSeed = Math.Floor(cr.Next() * 1e6);
            Particle np = Rent();
            np.X = p.X; np.Y = p.Y;
            np.Vx = Math.Cos(ang) * spd; np.Vy = Math.Sin(ang) * spd;
            np.Age = 0; np.Life = life;
            np.Size0 = Math.Max(1, p.Size0 * Or(em.BranchSizeScale, 0.7));
            np.Rot = p.Rot; np.Spin = p.Spin;
            np.Seed = nSeed; np.Vi = p.Vi;
            np.Gen = p.Gen + 1; np.StepN = 0;
            np.Tier = p.Tier; np.SubAcc = 0; np.PSeg = p.PSeg;
            if (pt.Trail) np.StartHist(p.X, p.Y);
            Parts.Add(np);
            if (p.Tier > 0) _subN++;
        }

        private static double Or(double v, double d) => v != 0 && !double.IsNaN(v) ? v : d;

        /// <summary>Math.hypot без промежуточного переполнения (как в JS).</summary>
        private static double Hypot(double x, double y)
        {
            x = Math.Abs(x); y = Math.Abs(y);
            double m = Math.Max(x, y);
            if (m == 0) return 0;
            double a = x / m, b = y / m;
            return m * Math.Sqrt(a * a + b * b);
        }

        private void Step()
        {
            double tl = T;
            Layer layer = _layer;
            EmitterSettings em = layer.Em;
            ParticleSettings pt = layer.Pt;
            double win = Math.Max(0, layer.End - layer.Start);
            bool ffOn = ForceStep(tl);
            List<double> ff = _fbuf;

            PathSettings path = em.Path;
            PathBake pb = null;
            bool guide = false;
            double gAt = 0, gLock = 0, gFlow = 0;
            bool gFast = false;
            _pe = null;
            _pe2 = null;
            if (path.Active)
            {
                pb = _pbuf;
                pb.Bake(path, tl, em.X.Eval(tl), em.Y.Eval(tl));
                if (path.Mode == PathMode.Emit || path.Mode == PathMode.Both)
                {
                    _pe = pb;
                    _pjit = Math.Max(0, path.Jitter.Eval(tl));
                    if (em.SubStep)
                    {
                        _pbuf2.Bake(path, tl + DT, em.X.Eval(tl + DT), em.Y.Eval(tl + DT));
                        _pe2 = _pbuf2;
                    }
                }
                if (path.Mode == PathMode.Guide || path.Mode == PathMode.Both)
                {
                    double at = Math.Max(0, path.Attract.Eval(tl));
                    double lk = Js.Clamp(path.Lock.Eval(tl), 0, 1);
                    double fl = path.Flow.Eval(tl);
                    if (at > 0 || lk > 0 || fl != 0)
                    {
                        guide = true;
                        gAt = at; gLock = lk; gFlow = fl; gFast = path.Fast;
                    }
                }
            }

            if (tl <= win + 1e-9)
            {
                double rate = Math.Max(0, em.Rate.Eval(tl));
                if (rate > 0)
                {
                    double a0 = _acc, inc = rate * DT;
                    _acc += inc;
                    double n = Math.Floor(_acc);
                    if (n > 0)
                    {
                        _acc -= n;
                        Spawn((int)n, tl, a0, em.SubStep ? inc : 0);
                    }
                }
                while (_bi < em.Bursts.Length && em.Bursts[_bi].T <= tl + 1e-9)
                {
                    Burst b = em.Bursts[_bi++];
                    if (b.T <= win + 1e-9) Spawn((int)Math.Max(0, Js.Round(b.N)), tl, 0, 0);
                }
            }

            Phys ph0 = PhysOf(pt, tl);
            bool subOn = em.Sub.On;
            Phys ph1 = subOn ? PhysOf(em.Sub.Pt, tl) : ph0;
            SubCtx sc = subOn ? MakeSubCtx(em, tl) : null;
            bool brOn = em.BranchChance > 0;
            double maxGenD = Math.Max(1, Or(em.BranchMaxGen, 2));
            List<Particle> parts = Parts;
            bool anyDead = false;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                Particle p = parts[i];
                Phys ph = p.Tier > 0 ? ph1 : ph0;
                p.Age += DT;
                p.StepN++;
                if (p.Age >= p.Life)
                {
                    if (sc != null && sc.EmitDeath && p.Tier < sc.MaxGen) SpawnSub(p, sc.Count, 20001, sc);
                    if (p.Tier > 0) _subN--;
                    p.Dead = true;
                    _live--;
                    anyDead = true;
                    continue;
                }
                if (sc != null && sc.EmitLife && p.Tier < sc.MaxGen && p.Age >= p.Life * sc.StartAt)
                {
                    p.SubAcc += sc.Rate * sc.GenScale[p.Tier + 1] * DT;
                    double n = Math.Floor(p.SubAcc);
                    if (n > 0)
                    {
                        p.SubAcc -= n;
                        SpawnSub(p, (int)n, 1, sc);
                    }
                }
                if (brOn && p.Gen < maxGenD && Js.Hash01(p.StepN * 7.13, p.Seed) < em.BranchChance * DT)
                {
                    SpawnBranch(p);
                }
                if (ph.Zig > 0)
                {
                    double dAng = (Js.Hash01(p.StepN * 3.7, p.Seed + 555) * 2 - 1) * ph.Zig * DT;
                    double ca = Math.Cos(dAng), sa = Math.Sin(dAng);
                    double nvx = p.Vx * ca - p.Vy * sa;
                    p.Vy = p.Vx * sa + p.Vy * ca;
                    p.Vx = nvx;
                }
                double ax = ph.Gx, ay = ph.Gy;
                double wx = 0, wy = 0;
                if (ph.Curl ? (ph.Ta > 0 || ph.Tw != 0) : ph.Ta > 0)
                {
                    double g = ph.Tramp > 0
                        ? (1 - ph.Tramp + ph.Tramp * Js.Smoothstep(0.04, 0.34, p.Age > 0 ? p.Age / p.Life : 0))
                        : 1;
                    if (ph.Curl)
                    {
                        Js.Curl2(p.X, p.Y + ph.Tri * tl, tl * ph.Tf, ph.Tsc, ph.Toc, ph.Tsd, out double cvx, out double cvy);
                        if (ph.Ta > 0) { ax += ph.Ta * g * cvx; ay += ph.Ta * g * cvy; }
                        if (ph.Tw != 0) { double w = ph.Tw * g; wx = cvx * w; wy = cvy * w; }
                    }
                    else
                    {
                        ax += ph.Ta * g * Js.Noise1(p.Age * ph.Tf, p.Seed);
                        ay += ph.Ta * g * Js.Noise1(p.Age * ph.Tf, p.Seed + 7777);
                    }
                }
                if (ffOn)
                {
                    for (int q = 0; q < ff.Count; q += FfN)
                    {
                        double rx = ff[q + 3], ry = ff[q + 4];
                        double dx = (p.X - ff[q + 1]) / rx, dy = (p.Y - ff[q + 2]) / ry;
                        double d2 = dx * dx + dy * dy;
                        if (d2 >= 1) continue;
                        double fo = ff[q + 6];
                        double u = 1 - Math.Sqrt(d2);
                        double w = fo == 1 ? u : (fo == 2 ? u * u : (fo == 0 ? 1 : Math.Pow(u, fo)));
                        if (w <= 0) continue;
                        double ux = dx / rx, uy = dy / ry;
                        double ul = Math.Sqrt(ux * ux + uy * uy);
                        if (ul < 1e-9) continue;
                        ux /= ul; uy /= ul;
                        double s = ff[q + 5] * w;
                        double md = ff[q];
                        if (md == 2) { ax -= uy * s; ay += ux * s; }
                        else if (md == 1) { ax += ux * s; ay += uy * s; }
                        else { ax -= ux * s; ay -= uy * s; }
                    }
                }
                bool hasGn = guide && p.Tier == 0;
                PathBake.Near gn = default;
                if (hasGn)
                {
                    gn = pb.Nearest(p.X, p.Y, gFast ? p.PSeg : -1, PWin);
                    if (gFast) p.PSeg = gn.I;
                    if (gAt > 0)
                    {
                        ax += (gn.X - p.X) * gAt;
                        ay += (gn.Y - p.Y) * gAt;
                    }
                }
                if (wx != 0 || wy != 0)
                {
                    p.Vx = wx + (p.Vx + ax * DT - wx) * ph.DragF;
                    p.Vy = wy + (p.Vy + ay * DT - wy) * ph.DragF;
                }
                else
                {
                    p.Vx = (p.Vx + ax * DT) * ph.DragF;
                    p.Vy = (p.Vy + ay * DT) * ph.DragF;
                }
                if (hasGn)
                {
                    if (gFlow != 0)
                    {
                        double dv = (gFlow - (p.Vx * gn.Tx + p.Vy * gn.Ty)) * FlowK;
                        p.Vx += gn.Tx * dv;
                        p.Vy += gn.Ty * dv;
                    }
                    if (gLock > 0)
                    {
                        double vn = p.Vx * -gn.Ty + p.Vy * gn.Tx;
                        p.Vx -= -gn.Ty * vn * gLock;
                        p.Vy -= gn.Tx * vn * gLock;
                    }
                }
                p.X += p.Vx * DT;
                p.Y += p.Vy * DT;
                if (hasGn && gLock > 0)
                {
                    PathBake.Near sn = pb.Nearest(p.X, p.Y, gFast ? gn.I : -1, PWin);
                    p.X += (sn.X - p.X) * gLock;
                    p.Y += (sn.Y - p.Y) * gLock;
                    if (gFast) p.PSeg = sn.I;
                }
                p.Rot += p.Spin * DT;
                if (ph.Trail)
                {
                    if (!p.HasHist) p.StartHist(p.X, p.Y);
                    else if ((p.StepN & 1) == 0)
                    {
                        List<double> h = p.Hist;
                        h.Add(p.X);
                        h.Add(p.Y);
                        if (h.Count > 480) h.RemoveRange(0, 2);
                    }
                }
            }
            // уплотнение с сохранением порядка — ровно то, что дал бы splice в цикле
            if (anyDead)
            {
                int w = 0;
                for (int r = 0; r < parts.Count; r++)
                {
                    Particle q = parts[r];
                    if (q.Dead) { _free.Push(q); continue; }
                    parts[w++] = q;
                }
                parts.RemoveRange(w, parts.Count - w);
            }
            T += DT;
        }
    }
}
