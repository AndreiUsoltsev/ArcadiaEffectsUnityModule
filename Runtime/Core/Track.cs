using System;
using System.Collections.Generic;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Анимационный трек (js/core/track.js): статика — число (или цвет [r,g,b]),
    /// анимация — <c>{keys:[{t,v,i,o}]}</c>. Время ключей — секунды от начала слоя.
    /// </summary>
    public sealed class Track
    {
        private enum Ease : byte { Lin, Ease, Hold, Cust }

        private struct Key
        {
            public double T;
            public double V;
            public double R, G, B;
            public Ease In, Out;
            public double InX, InY, OutX, OutY;
            public bool InH, OutH;
        }

        private readonly double _value;
        private readonly double _r = 255, _g = 255, _b = 255;
        private readonly Key[] _keys;
        private readonly bool _color;

        public static readonly Track Zero = new Track(0);
        public static readonly Track One = new Track(1);

        public Track(double value) { _value = value; }

        public Track(double r, double g, double b)
        {
            _color = true;
            _r = r; _g = g; _b = b;
        }

        private Track(Key[] keys, bool color)
        {
            _keys = keys;
            _color = color;
        }

        public bool IsAnimated => _keys != null;

        /// <summary>Значение числового трека в момент <paramref name="t"/> (время слоя).</summary>
        public double Eval(double t)
        {
            if (_keys == null) return _value;
            int n = _keys.Length;
            if (n == 0) return 0;
            if (_color) return _keys[0].V;
            if (t <= _keys[0].T) return _keys[0].V;
            if (t >= _keys[n - 1].T) return _keys[n - 1].V;
            int lo = Segment(t);
            Key a = _keys[lo], b = _keys[lo + 1];
            if (a.Out == Ease.Hold) return a.V;
            double f = Factor(a, b, t);
            return a.V + (b.V - a.V) * f;
        }

        /// <summary>Значение цветового трека, каналы 0..255 (без округления).</summary>
        public void EvalColor(double t, out double r, out double g, out double b)
        {
            if (_keys == null)
            {
                if (_color) { r = _r; g = _g; b = _b; }
                else { r = g = b = 255; } // не цвет — движок подставляет белый
                return;
            }
            int n = _keys.Length;
            if (n == 0 || !_color) { r = g = b = 255; return; }
            if (t <= _keys[0].T) { r = _keys[0].R; g = _keys[0].G; b = _keys[0].B; return; }
            if (t >= _keys[n - 1].T) { r = _keys[n - 1].R; g = _keys[n - 1].G; b = _keys[n - 1].B; return; }
            int lo = Segment(t);
            Key ka = _keys[lo], kb = _keys[lo + 1];
            if (ka.Out == Ease.Hold) { r = ka.R; g = ka.G; b = ka.B; return; }
            double f = Factor(ka, kb, t);
            r = ka.R + (kb.R - ka.R) * f;
            g = ka.G + (kb.G - ka.G) * f;
            b = ka.B + (kb.B - ka.B) * f;
        }

        private int Segment(double t)
        {
            int lo = 0;
            for (int k = 1; k < _keys.Length; k++)
            {
                if (_keys[k].T > t) { lo = k - 1; break; }
            }
            return lo;
        }

        private static double Factor(Key a, Key b, double t)
        {
            double span = b.T - a.T;
            double u = span > 1e-9 ? (t - a.T) / span : 1;
            double p1x = 1.0 / 3, p1y = 1.0 / 3, p2x = 2.0 / 3, p2y = 2.0 / 3;
            if (a.Out == Ease.Ease) { p1x = 0.38; p1y = 0; }
            else if (a.Out == Ease.Cust && a.OutH) { p1x = Js.Clamp(a.OutX, 0, 1); p1y = a.OutY; }
            if (b.In == Ease.Ease) { p2x = 0.62; p2y = 1; }
            else if (b.In == Ease.Cust && b.InH) { p2x = Js.Clamp(b.InX, 0, 1); p2y = b.InY; }
            return Js.BezierEase(p1x, p1y, p2x, p2y, u);
        }

        // ---------- разбор ----------

        /// <summary>Числовой трек из JSON-значения; null — дефолт.</summary>
        public static Track Parse(object o, double def)
        {
            if (o == null) return def == 0 ? Zero : (def == 1 ? One : new Track(def));
            if (o is double d) return new Track(d);
            if (o is bool bo) return new Track(bo ? 1 : 0);
            if (o is Dictionary<string, object> obj && obj.TryGetValue("keys", out object ko) && ko is List<object> kl)
            {
                bool color = kl.Count > 0 && kl[0] is Dictionary<string, object> k0 && k0.TryGetValue("v", out object v0) && v0 is List<object>;
                return new Track(ParseKeys(kl), color);
            }
            if (o is List<object> arr && arr.Count >= 3)
                return new Track(ModelUtil.ToNum(arr[0]), ModelUtil.ToNum(arr[1]), ModelUtil.ToNum(arr[2]));
            return new Track(def);
        }

        /// <summary>Цветовой трек ([r,g,b] или ключи с v=[r,g,b]); null — дефолт.</summary>
        public static Track ParseColor(object o, double r, double g, double b)
        {
            if (o == null) return new Track(r, g, b);
            if (o is List<object> arr && arr.Count >= 3)
                return new Track(ModelUtil.ToNum(arr[0]), ModelUtil.ToNum(arr[1]), ModelUtil.ToNum(arr[2]));
            if (o is Dictionary<string, object> obj && obj.TryGetValue("keys", out object ko) && ko is List<object> kl)
            {
                bool color = kl.Count > 0 && kl[0] is Dictionary<string, object> k0 && k0.TryGetValue("v", out object v0) && v0 is List<object>;
                return new Track(ParseKeys(kl), color);
            }
            // статика-число в цветовом поле движок рисует белым
            return new Track(0);
        }

        private static Key[] ParseKeys(List<object> list)
        {
            var keys = new Key[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var k = new Key { In = Ease.Lin, Out = Ease.Lin, R = 255, G = 255, B = 255 };
                if (list[i] is Dictionary<string, object> d)
                {
                    k.T = ModelUtil.Num(d, "t", 0);
                    if (d.TryGetValue("v", out object v))
                    {
                        if (v is List<object> c && c.Count >= 3)
                        {
                            k.R = ModelUtil.ToNum(c[0]); k.G = ModelUtil.ToNum(c[1]); k.B = ModelUtil.ToNum(c[2]);
                        }
                        else k.V = ModelUtil.ToNum(v);
                    }
                    ParseEase(d, "i", out k.In, out k.InH, out k.InX, out k.InY);
                    ParseEase(d, "o", out k.Out, out k.OutH, out k.OutX, out k.OutY);
                }
                keys[i] = k;
            }
            return keys;
        }

        private static void ParseEase(Dictionary<string, object> d, string name, out Ease mode, out bool hasH, out double hx, out double hy)
        {
            mode = Ease.Lin; hasH = false; hx = 0; hy = 0;
            if (!d.TryGetValue(name, out object o) || !(o is Dictionary<string, object> e)) return;
            string m = ModelUtil.Str(e, "m", "lin");
            mode = m == "ease" ? Ease.Ease : m == "hold" ? Ease.Hold : m == "cust" ? Ease.Cust : Ease.Lin;
            if (e.TryGetValue("h", out object h) && h is List<object> hl && hl.Count >= 2)
            {
                hasH = true;
                hx = ModelUtil.ToNum(hl[0]);
                hy = ModelUtil.ToNum(hl[1]);
            }
        }
    }

    /// <summary>Кривая «за жизнь частицы» (Catmull-Rom с зажатыми концами), t: 0..1.</summary>
    public sealed class Curve
    {
        private readonly double[] _t;
        private readonly double[] _v;

        public Curve(double[] t, double[] v) { _t = t; _v = v; }

        public static Curve Make(params double[] tv)
        {
            int n = tv.Length / 2;
            var t = new double[n];
            var v = new double[n];
            for (int i = 0; i < n; i++) { t[i] = tv[i * 2]; v[i] = tv[i * 2 + 1]; }
            return new Curve(t, v);
        }

        public static readonly Curve Const1 = Make(0, 1, 1, 1);

        public int Count => _t.Length;

        public double Eval(double t)
        {
            int n = _t.Length;
            if (n == 0) return 1;
            if (n == 1 || t <= _t[0]) return _v[0];
            if (t >= _t[n - 1]) return _v[n - 1];
            int i = 0;
            while (i < n - 2 && _t[i + 1] <= t) i++;
            double p1 = _v[i], p2 = _v[i + 1];
            double p0 = _v[Math.Max(0, i - 1)], p3 = _v[Math.Min(n - 1, i + 2)];
            double span = _t[i + 1] - _t[i];
            if (span < 1e-9) return p2;
            double u = (t - _t[i]) / span;
            double u2 = u * u, u3 = u2 * u;
            double v = 0.5 * ((2 * p1) + (-p0 + p2) * u +
                              (2 * p0 - 5 * p1 + 4 * p2 - p3) * u2 +
                              (-p0 + 3 * p1 - 3 * p2 + p3) * u3);
            return Math.Max(0, v);
        }

        /// <summary>Значение константной кривой либо null, если кривая меняется.</summary>
        public double? ConstValue()
        {
            if (_v.Length == 0) return 1;
            double v = _v[0];
            for (int i = 1; i < _v.Length; i++) if (Math.Abs(_v[i] - v) > 1e-6) return null;
            return v;
        }

        public static Curve Parse(object o, Curve def)
        {
            if (!(o is Dictionary<string, object> d) || !d.TryGetValue("pts", out object po) || !(po is List<object> pts)) return def;
            var t = new double[pts.Count];
            var v = new double[pts.Count];
            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i] is Dictionary<string, object> p)
                {
                    t[i] = ModelUtil.Num(p, "t", 0);
                    v[i] = ModelUtil.Num(p, "v", 0);
                }
            }
            return new Curve(t, v);
        }
    }

    /// <summary>Градиент цвета за жизнь частицы.</summary>
    public sealed class ColorRamp
    {
        private readonly double[] _t;
        private readonly double[] _c; // r,g,b подряд

        public ColorRamp(double[] t, double[] c) { _t = t; _c = c; }

        /// <summary>Градиент из пар (t, "#rrggbb").</summary>
        public static ColorRamp Make(params object[] tc)
        {
            int n = tc.Length / 2;
            var t = new double[n];
            var c = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                t[i] = Convert.ToDouble(tc[i * 2]);
                ModelUtil.HexToRgb((string)tc[i * 2 + 1], out c[i * 3], out c[i * 3 + 1], out c[i * 3 + 2]);
            }
            return new ColorRamp(t, c);
        }

        public int Count => _t.Length;
        public double StopT(int i) => _t[i];
        public double StopC(int i, int ch) => _c[i * 3 + ch];

        public void Eval(double t, out double r, out double g, out double b)
        {
            int n = _t.Length;
            if (n == 0) { r = g = b = 255; return; }
            if (t <= _t[0]) { r = _c[0]; g = _c[1]; b = _c[2]; return; }
            if (t >= _t[n - 1]) { Stop(n - 1, out r, out g, out b); return; }
            for (int i = 0; i < n - 1; i++)
            {
                if (_t[i + 1] >= t)
                {
                    double span = _t[i + 1] - _t[i];
                    double u = span > 1e-9 ? (t - _t[i]) / span : 1;
                    r = _c[i * 3] + (_c[i * 3 + 3] - _c[i * 3]) * u;
                    g = _c[i * 3 + 1] + (_c[i * 3 + 4] - _c[i * 3 + 1]) * u;
                    b = _c[i * 3 + 2] + (_c[i * 3 + 5] - _c[i * 3 + 2]) * u;
                    return;
                }
            }
            Stop(n - 1, out r, out g, out b);
        }

        private void Stop(int i, out double r, out double g, out double b)
        {
            r = _c[i * 3]; g = _c[i * 3 + 1]; b = _c[i * 3 + 2];
        }

        /// <summary>
        /// Квантованный цвет, как <c>Grad.hexAt</c> (24 ступени, округление до байта):
        /// именно этим цветом редактор тонирует частицу.
        /// </summary>
        public void HexAt(double t, out byte r, out byte g, out byte b)
        {
            const double steps = 24;
            double q = Js.Round(Js.Clamp(t, 0, 1) * steps) / steps;
            Eval(q, out double fr, out double fg, out double fb);
            r = ModelUtil.ToByte(fr);
            g = ModelUtil.ToByte(fg);
            b = ModelUtil.ToByte(fb);
        }

        public static ColorRamp Parse(object o, ColorRamp def)
        {
            if (!(o is Dictionary<string, object> d) || !d.TryGetValue("stops", out object so) || !(so is List<object> stops)) return def;
            var t = new double[stops.Count];
            var c = new double[stops.Count * 3];
            for (int i = 0; i < stops.Count; i++)
            {
                if (!(stops[i] is Dictionary<string, object> s)) continue;
                t[i] = ModelUtil.Num(s, "t", 0);
                if (s.TryGetValue("c", out object co))
                {
                    if (co is List<object> cl && cl.Count >= 3)
                    {
                        c[i * 3] = ModelUtil.ToNum(cl[0]); c[i * 3 + 1] = ModelUtil.ToNum(cl[1]); c[i * 3 + 2] = ModelUtil.ToNum(cl[2]);
                    }
                    else if (co is string hex)
                    {
                        ModelUtil.HexToRgb(hex, out c[i * 3], out c[i * 3 + 1], out c[i * 3 + 2]);
                    }
                }
            }
            return new ColorRamp(t, c);
        }
    }
}
