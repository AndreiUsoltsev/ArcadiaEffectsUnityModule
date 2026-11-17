using System;
using System.Collections.Generic;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Процедурные белые спрайты редактора (js/core/sprites.js), нарисованные программно:
    /// мини-эмуляция Canvas2D (радиальные градиенты, заливка полигонов, трансформы, source-over).
    /// Результат — альфа 0..1, строки сверху вниз, белый цвет.
    /// </summary>
    internal static class ShapeRasterizer
    {
        public const int Base = 128;

        private sealed class Canvas
        {
            public readonly int W, H;
            public readonly float[] A;
            // текущая матрица: x' = a*x + c*y + e, y' = b*x + d*y + f
            private double _a = 1, _b, _c, _d = 1, _e, _f;
            private readonly Stack<double[]> _stack = new Stack<double[]>();

            public Canvas(int w, int h) { W = w; H = h; A = new float[w * h]; }

            public void Save() => _stack.Push(new[] { _a, _b, _c, _d, _e, _f });

            public void Restore()
            {
                var m = _stack.Pop();
                _a = m[0]; _b = m[1]; _c = m[2]; _d = m[3]; _e = m[4]; _f = m[5];
            }

            public void Translate(double x, double y)
            {
                _e += _a * x + _c * y;
                _f += _b * x + _d * y;
            }

            public void Scale(double sx, double sy)
            {
                _a *= sx; _b *= sx;
                _c *= sy; _d *= sy;
            }

            private void Inverse(double x, double y, out double lx, out double ly)
            {
                double det = _a * _d - _b * _c;
                double px = x - _e, py = y - _f;
                lx = (_d * px - _c * py) / det;
                ly = (-_b * px + _a * py) / det;
            }

            private void Forward(double x, double y, out double dx, out double dy)
            {
                dx = _a * x + _c * y + _e;
                dy = _b * x + _d * y + _f;
            }

            private void Compose(int idx, double src)
            {
                if (src <= 0) return;
                if (src > 1) src = 1;
                A[idx] = (float)(src + A[idx] * (1 - src));
            }

            /// <summary>
            /// Заливка круга (cx,cy,rArc) радиальным градиентом r0..r1 вокруг (cx,cy),
            /// стопы — пары (offset, alpha). Координаты локальные (под текущей матрицей).
            /// </summary>
            public void FillRadial(double cx, double cy, double rArc, double r0, double r1, double[] stops, int ss = 3)
            {
                // ограничивающий прямоугольник в устройстве
                Bounds(cx - rArc, cy - rArc, cx + rArc, cy + rArc, out int x0, out int y0, out int x1, out int y1);
                double inv = 1.0 / ss;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        double sum = 0;
                        for (int sy = 0; sy < ss; sy++)
                        {
                            for (int sx = 0; sx < ss; sx++)
                            {
                                Inverse(x + (sx + 0.5) * inv, y + (sy + 0.5) * inv, out double lx, out double ly);
                                double dx = lx - cx, dy = ly - cy;
                                double d = Math.Sqrt(dx * dx + dy * dy);
                                if (d > rArc) continue;
                                double t = r1 > r0 ? (d - r0) / (r1 - r0) : 1;
                                sum += StopAlpha(stops, t);
                            }
                        }
                        Compose(y * W + x, sum / (ss * ss));
                    }
                }
            }

            /// <summary>Сплошной круг (локальные координаты).</summary>
            public void FillCircle(double cx, double cy, double r)
            {
                FillRadial(cx, cy, r, 0, 1, new double[] { 0, 1, 1, 1 }, 4);
            }

            /// <summary>Сплошной полигон по правилу nonzero (локальные координаты).</summary>
            public void FillPolygon(List<double> pts)
            {
                int n = pts.Count / 2;
                var dx = new double[n];
                var dy = new double[n];
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                for (int i = 0; i < n; i++)
                {
                    Forward(pts[i * 2], pts[i * 2 + 1], out dx[i], out dy[i]);
                    minX = Math.Min(minX, dx[i]); maxX = Math.Max(maxX, dx[i]);
                    minY = Math.Min(minY, dy[i]); maxY = Math.Max(maxY, dy[i]);
                }
                int x0 = Math.Max(0, (int)Math.Floor(minX) - 1), x1 = Math.Min(W - 1, (int)Math.Ceiling(maxX) + 1);
                int y0 = Math.Max(0, (int)Math.Floor(minY) - 1), y1 = Math.Min(H - 1, (int)Math.Ceiling(maxY) + 1);
                const int ss = 4;
                const double inv = 1.0 / ss;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int hits = 0;
                        for (int sy = 0; sy < ss; sy++)
                        {
                            double py = y + (sy + 0.5) * inv;
                            for (int sx = 0; sx < ss; sx++)
                            {
                                double px = x + (sx + 0.5) * inv;
                                if (Winding(dx, dy, n, px, py) != 0) hits++;
                            }
                        }
                        Compose(y * W + x, (double)hits / (ss * ss));
                    }
                }
            }

            private static int Winding(double[] xs, double[] ys, int n, double px, double py)
            {
                int w = 0;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double ay = ys[j], by = ys[i];
                    if (ay <= py)
                    {
                        if (by > py && Cross(xs[j], ay, xs[i], by, px, py) > 0) w++;
                    }
                    else if (by <= py && Cross(xs[j], ay, xs[i], by, px, py) < 0) w--;
                }
                return w;
            }

            private static double Cross(double ax, double ay, double bx, double by, double px, double py) =>
                (bx - ax) * (py - ay) - (px - ax) * (by - ay);

            private void Bounds(double lx0, double ly0, double lx1, double ly1, out int x0, out int y0, out int x1, out int y1)
            {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                for (int i = 0; i < 4; i++)
                {
                    Forward((i & 1) == 0 ? lx0 : lx1, (i & 2) == 0 ? ly0 : ly1, out double x, out double y);
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
                x0 = Math.Max(0, (int)Math.Floor(minX) - 1); x1 = Math.Min(W - 1, (int)Math.Ceiling(maxX) + 1);
                y0 = Math.Max(0, (int)Math.Floor(minY) - 1); y1 = Math.Min(H - 1, (int)Math.Ceiling(maxY) + 1);
            }
        }

        /// <summary>Альфа градиента: стопы (offset, alpha) подряд, кламп по краям.</summary>
        private static double StopAlpha(double[] st, double t)
        {
            int n = st.Length / 2;
            if (t <= st[0]) return st[1];
            if (t >= st[(n - 1) * 2]) return st[(n - 1) * 2 + 1];
            for (int i = 0; i < n - 1; i++)
            {
                double t0 = st[i * 2], t1 = st[i * 2 + 2];
                if (t <= t1)
                {
                    double u = t1 > t0 ? (t - t0) / (t1 - t0) : 1;
                    return st[i * 2 + 1] + (st[i * 2 + 3] - st[i * 2 + 1]) * u;
                }
            }
            return st[(n - 1) * 2 + 1];
        }

        private static readonly double[] SoftStops = { 0, 1, 0.3, 0.9, 0.65, 0.42, 1, 0 };

        private static void SoftCircle(Canvas c, double cx, double cy, double r, double alpha = 1, double[] stops = null)
        {
            var s = (double[])(stops ?? SoftStops).Clone();
            for (int i = 1; i < s.Length; i += 2) s[i] *= alpha;
            c.FillRadial(cx, cy, r, 0, r, s);
        }

        private static double Clamp(double v, double a, double b) => v < a ? a : (v > b ? b : v);

        /// <summary>Нарисовать фигуру 128x128 (белая, альфа сверху вниз).</summary>
        public static float[] Draw(string id, SpriteRef p, int variant)
        {
            var c = new Canvas(Base, Base);
            double var = variant;
            switch (id)
            {
                case "dot":
                    SoftCircle(c, 64, 64, 59, 1, new double[] { 0, 1, 0.86, 1, 1, 0 });
                    break;
                case "ring":
                {
                    double th = Clamp(p?.Th ?? 0.14, 0.02, 0.9);
                    double rOut = 60, rIn = rOut * (1 - th);
                    double rm = (rOut + rIn) / 2, half = (rOut - rIn) / 2;
                    var st = new double[22];
                    for (int i = 0; i <= 10; i++)
                    {
                        double rr = rIn - 2 + (rOut + 2 - (rIn - 2)) * i / 10;
                        double a = Clamp(1 - Math.Abs((rr - rm) / (half + 2)), 0, 1);
                        st[i * 2] = i / 10.0;
                        st[i * 2 + 1] = Math.Round(a, 3);
                    }
                    c.FillRadial(64, 64, rOut + 2, Math.Max(0, rIn - 2), rOut + 2, st);
                    break;
                }
                case "square":
                    c.FillPolygon(new List<double> { 20, 20, 108, 20, 108, 108, 20, 108 });
                    break;
                case "poly":
                {
                    int n = (int)Clamp(Js.Round(Or(p?.N, 6)), 3, 16);
                    var pts = new List<double>();
                    for (int i = 0; i < n; i++)
                    {
                        double a = -Math.PI / 2 + (double)i / n * Math.PI * 2;
                        pts.Add(64 + Math.Cos(a) * 58);
                        pts.Add(64 + Math.Sin(a) * 58);
                    }
                    c.FillPolygon(pts);
                    break;
                }
                case "star":
                {
                    int n = (int)Clamp(Js.Round(Or(p?.N, 5)), 3, 12);
                    double inner = Clamp(p?.Inr ?? 0.5, 0.05, 0.95);
                    var pts = new List<double>();
                    for (int i = 0; i < n * 2; i++)
                    {
                        double a = -Math.PI / 2 + (double)i / (n * 2) * Math.PI * 2;
                        double r = (i % 2 == 0 ? 1 : inner) * 58;
                        pts.Add(64 + Math.Cos(a) * r);
                        pts.Add(64 + Math.Sin(a) * r);
                    }
                    c.FillPolygon(pts);
                    break;
                }
                case "streak":
                    c.Save();
                    c.Translate(64, 64); c.Scale(1, 0.16);
                    SoftCircle(c, 0, 0, 62, 1, new double[] { 0, 1, 0.25, 0.95, 0.7, 0.4, 1, 0 });
                    c.Restore();
                    break;
                case "spark":
                    c.Save();
                    c.Translate(64, 64);
                    c.Scale(1, 0.1); SoftCircle(c, 0, 0, 62);
                    c.Restore();
                    c.Save();
                    c.Translate(64, 64);
                    c.Scale(0.1, 1); SoftCircle(c, 0, 0, 62);
                    c.Restore();
                    SoftCircle(c, 64, 64, 18);
                    break;
                case "smoke":
                {
                    var rng = new Js.Mulberry32(Js.ToInt32(1000 + var * 77));
                    for (int i = 0; i < 9; i++)
                    {
                        double a = rng.Next() * Math.PI * 2;
                        double d = 8 + rng.Next() * 24;
                        double r = 22 + rng.Next() * 22;
                        double al = 0.55 + rng.Next() * 0.4;
                        SoftCircle(c, 64 + Math.Cos(a) * d, 64 + Math.Sin(a) * d, r, al, new double[] { 0, 1, 0.45, 0.8, 1, 0 });
                    }
                    break;
                }
                case "blob":
                {
                    var rng = new Js.Mulberry32(Js.ToInt32(500 + var * 31));
                    int bumps = 7 + (int)Math.Floor(rng.Next() * 3);
                    double phase = rng.Next() * Math.PI * 2;
                    var pts = new List<double>();
                    const int steps = 48;
                    for (int i = 0; i <= steps; i++)
                    {
                        double a = (double)i / steps * Math.PI * 2;
                        double w = Math.Sin(a * bumps + phase) * 0.5 + Math.Sin(a * (bumps - 3) - phase * 2) * 0.5;
                        double r = 44 + w * 9;
                        pts.Add(64 + Math.Cos(a) * r);
                        pts.Add(64 + Math.Sin(a) * r);
                    }
                    c.FillPolygon(pts);
                    for (int i = 0; i < 4; i++)
                    {
                        double a = rng.Next() * Math.PI * 2, d = 30 + rng.Next() * 14, r = 10 + rng.Next() * 14;
                        c.FillCircle(64 + Math.Cos(a) * d, 64 + Math.Sin(a) * d, r);
                    }
                    break;
                }
                case "flame":
                {
                    var rng = new Js.Mulberry32(Js.ToInt32(300 + var * 97));
                    double taper = Clamp(p?.Taper ?? 0.55, 0.1, 1);
                    double bend = (rng.Next() * 2 - 1) * 18;
                    double hump = 0.3 + rng.Next() * 0.22;
                    double wob = 0.6 + rng.Next() * 0.8;
                    double wph = rng.Next() * Math.PI * 2;
                    double half = 31 * (0.85 + rng.Next() * 0.3);
                    const int N = 26;
                    for (int i = 0; i <= N; i++)
                    {
                        double t = (double)i / N;
                        double body = Math.Pow(Math.Sin(Math.PI * Math.Pow(t, hump)), 0.75);
                        double tip = Math.Pow(1 - t, taper);
                        double prof = Math.Max(0, body * tip + 0.12 * (1 - t) * (1 - t));
                        double r = prof * half * (1 + 0.1 * Math.Sin(t * 9 * wob + wph));
                        if (r < 1.2) continue;
                        SoftCircle(c, 14 + t * 108, 64 + Math.Sin(t * 2.1 + wph) * bend * t, r, 0.3, new double[] { 0, 1, 0.42, 0.72, 1, 0 });
                    }
                    break;
                }
                case "ember":
                    SoftCircle(c, 64, 64, 62, 1, new double[] { 0, 1, 0.12, 1, 0.22, 0.55, 0.45, 0.2, 1, 0 });
                    break;
                case "shard":
                {
                    var rng = new Js.Mulberry32(Js.ToInt32(900 + var * 53));
                    int n = 4 + (int)Math.Floor(rng.Next() * 2);
                    var pts = new List<double>();
                    for (int i = 0; i < n; i++)
                    {
                        double a = (double)i / n * Math.PI * 2 + rng.Next() * 0.8;
                        double r = (i % 2 == 1 ? 24 : 52) * (0.6 + rng.Next() * 0.5);
                        pts.Add(64 + Math.Cos(a) * r * 1.15);
                        pts.Add(64 + Math.Sin(a) * r * 0.7);
                    }
                    c.FillPolygon(pts);
                    break;
                }
                default: // soft и неизвестные id
                    SoftCircle(c, 64, 64, 62);
                    break;
            }
            return c.A;
        }

        private static double Or(double? v, double d) => v.HasValue && v.Value != 0 && !double.IsNaN(v.Value) ? v.Value : d;

        /// <summary>
        /// Свечение (<c>Sprites.getGlow</c>): спрайт ужат в центр холста 192 (на 96 px) и размыт
        /// гауссом с sigma = max(0.5, bq*96). Вход и выход — альфа, строки сверху вниз.
        /// </summary>
        public static float[] Glow(float[] src, int sw, int sh, double blurQ)
        {
            const int w = 192;
            var dst = new float[w * w];
            // drawImage(tinted, 48, 48, 96, 96) — площадное усреднение (даунскейл ~1.33x)
            double kx = (double)sw / 96, ky = (double)sh / 96;
            for (int y = 0; y < 96; y++)
            {
                for (int x = 0; x < 96; x++)
                {
                    dst[(y + 48) * w + x + 48] = (float)AreaSample(src, sw, sh, x * kx, y * ky, (x + 1) * kx, (y + 1) * ky);
                }
            }
            double sigma = Math.Max(0.5, blurQ * w * 0.5);
            return Blur(dst, w, w, sigma);
        }

        private static double AreaSample(float[] a, int w, int h, double x0, double y0, double x1, double y1)
        {
            double sum = 0, area = 0;
            int ix0 = (int)Math.Floor(x0), ix1 = (int)Math.Ceiling(x1), iy0 = (int)Math.Floor(y0), iy1 = (int)Math.Ceiling(y1);
            for (int y = iy0; y < iy1; y++)
            {
                if (y < 0 || y >= h) continue;
                double wy = Math.Min(y + 1, y1) - Math.Max(y, y0);
                if (wy <= 0) continue;
                for (int x = ix0; x < ix1; x++)
                {
                    if (x < 0 || x >= w) continue;
                    double wx = Math.Min(x + 1, x1) - Math.Max(x, x0);
                    if (wx <= 0) continue;
                    sum += a[y * w + x] * wx * wy;
                    area += wx * wy;
                }
            }
            return area > 0 ? sum / area : 0;
        }

        /// <summary>Гауссов блюр с прозрачной границей (как CSS blur на холсте).</summary>
        public static float[] Blur(float[] src, int w, int h, double sigma)
        {
            int r = (int)Math.Ceiling(sigma * 3);
            var k = new double[r * 2 + 1];
            double ks = 0;
            for (int i = -r; i <= r; i++) { k[i + r] = Math.Exp(-(i * i) / (2 * sigma * sigma)); ks += k[i + r]; }
            for (int i = 0; i < k.Length; i++) k[i] /= ks;
            var tmp = new float[w * h];
            var dst = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int xx = x + i;
                        if (xx < 0 || xx >= w) continue;
                        s += src[y * w + xx] * k[i + r];
                    }
                    tmp[y * w + x] = (float)s;
                }
            }
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double s = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int yy = y + i;
                        if (yy < 0 || yy >= h) continue;
                        s += tmp[yy * w + x] * k[i + r];
                    }
                    dst[y * w + x] = (float)s;
                }
            }
            return dst;
        }
    }
}
