using System;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Арифметика JavaScript и детерминированные генераторы редактора (js/core/utils.js),
    /// перенесённые один в один: те же <c>|0</c>, <c>Math.imul</c>, <c>&gt;&gt;&gt;</c>, mulberry32,
    /// value-noise и curl-поля. От этого зависит, что симуляция в Unity совпадает с редактором.
    /// </summary>
    public static class Js
    {
        public const double DEG = Math.PI / 180.0;

        /// <summary>JS <c>x | 0</c> (ToInt32).</summary>
        public static int ToInt32(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            double t = Math.Truncate(v);
            if (t >= int.MinValue && t <= int.MaxValue) return (int)t;
            double m = t % 4294967296.0;
            if (m < 0) m += 4294967296.0;
            return unchecked((int)(uint)m);
        }

        /// <summary>JS <c>Math.round</c>: половина округляется вверх.</summary>
        public static double Round(double x) => Math.Floor(x + 0.5);

        public static double Clamp(double v, double a, double b) => v < a ? a : (v > b ? b : v);

        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        public static double Smoothstep(double a, double b, double x)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            double t = Clamp(b == a ? (x < a ? 0 : 1) : (x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        // ---------- детерминированный rng ----------

        /// <summary>mulberry32 — тот же поток чисел, что <c>AFX.mulberry32</c>.</summary>
        public sealed class Mulberry32
        {
            private int _a;

            public Mulberry32(int seed)
            {
                _a = unchecked(seed + (int)0x9E3779B9);
            }

            public double Next()
            {
                unchecked
                {
                    _a = _a + 0x6D2B79F5;
                    int a = _a;
                    int t = (a ^ (int)((uint)a >> 15)) * (1 | a);
                    t = (t + (t ^ (int)((uint)t >> 7)) * (61 | t)) ^ t;
                    return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
                }
            }
        }

        public static double Hash01(double i, double seed)
        {
            double s = Math.Sin(i * 127.1 + seed * 311.7) * 43758.5453123;
            return s - Math.Floor(s);
        }

        /// <summary>value-noise 1D, результат -1..1.</summary>
        public static double Noise1(double x, double seed)
        {
            double i = Math.Floor(x), f = x - i, u = f * f * (3 - 2 * f);
            return Lerp(Hash01(i, seed), Hash01(i + 1, seed), u) * 2 - 1;
        }

        public static double Hash2(int i, int j, int seed)
        {
            unchecked
            {
                int h = (i * 0x27d4eb2d) ^ (j * 0x165667b1) ^ (seed * (int)0x9e3779b1);
                h = (h ^ (int)((uint)h >> 15)) * (int)0x85ebca6b;
                h = (h ^ (int)((uint)h >> 13)) * (int)0xc2b2ae35;
                return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
            }
        }

        /// <summary>value-noise 2D, результат 0..1.</summary>
        public static double Noise2(double x, double y, int seed)
        {
            double fi = Math.Floor(x), fj = Math.Floor(y);
            double fx = x - fi, fy = y - fj;
            double ux = fx * fx * (3 - 2 * fx), uy = fy * fy * (3 - 2 * fy);
            int i = ToInt32(fi), j = ToInt32(fj);
            double a = Hash2(i, j, seed), b = Hash2(unchecked(i + 1), j, seed);
            double c = Hash2(i, unchecked(j + 1), seed), d = Hash2(unchecked(i + 1), unchecked(j + 1), seed);
            return Lerp(a + (b - a) * ux, c + (d - c) * ux, uy);
        }

        /// <summary>value-noise 3D: z — ось времени, два 2D-слоя лерпуются.</summary>
        public static double Noise3(double x, double y, double z, double seed)
        {
            double k = Math.Floor(z), fz = z - k, uz = fz * fz * (3 - 2 * fz);
            return Lerp(Noise2(x, y, ToInt32(seed + k * 1013)), Noise2(x, y, ToInt32(seed + (k + 1) * 1013)), uz);
        }

        private const double CurlE = 0.5;
        private const double CurlLac = 2.17;

        /// <summary>Бездивергентное вихревое поле (<c>AFX.curl2</c>).</summary>
        public static void Curl2(double x, double y, double z, double scale, double oct, double seed, out double ox, out double oy)
        {
            double inv = 1 / Math.Max(1e-3, scale);
            int n = oct < 1 ? 1 : (oct > 4 ? 4 : ToInt32(oct));
            double vx = 0, vy = 0, amp = 1, fr = inv, tz = z, norm = 0, offx = 0, offy = 0;
            for (int o = 0; o < n; o++)
            {
                double px = x * fr + offx, py = y * fr + offy;
                vx += amp * (Noise3(px, py + CurlE, tz, seed) - Noise3(px, py - CurlE, tz, seed));
                vy -= amp * (Noise3(px + CurlE, py, tz, seed) - Noise3(px - CurlE, py, tz, seed));
                norm += amp;
                amp *= 0.5; fr *= CurlLac; tz *= CurlLac; seed += 7717;
                offx += 31.416; offy += 17.234;
            }
            double kk = norm > 0 ? 1 / norm : 0;
            ox = vx * kk;
            oy = vy * kk;
        }

        /// <summary>Турбулентный fBm по модулю шума (<c>AFX.turb3</c>), 0..1.</summary>
        public static double Turb3(double x, double y, double z, double oct, double seed)
        {
            int n = oct < 1 ? 1 : (oct > 4 ? 4 : ToInt32(oct));
            double v = 0, amp = 1, fr = 1, tz = z, norm = 0;
            for (int o = 0; o < n; o++)
            {
                v += amp * Math.Abs(Noise3(x * fr, y * fr, tz, seed) * 2 - 1);
                norm += amp;
                amp *= 0.5; fr *= 2.13; tz *= 2.13; seed += 5387;
            }
            return v / norm;
        }

        /// <summary>Вихрь турбулентного шума (<c>AFX.turbCurl3</c>).</summary>
        public static void TurbCurl3(double x, double y, double z, double oct, double seed, out double ox, out double oy)
        {
            int n = oct < 1 ? 1 : (oct > 4 ? 4 : ToInt32(oct));
            const double E = 0.4;
            double vx = 0, vy = 0, amp = 1, fr = 1, tz = z, norm = 0;
            for (int o = 0; o < n; o++)
            {
                double px = x * fr, py = y * fr;
                vx += amp * (Math.Abs(Noise3(px, py + E, tz, seed) * 2 - 1) - Math.Abs(Noise3(px, py - E, tz, seed) * 2 - 1));
                vy -= amp * (Math.Abs(Noise3(px + E, py, tz, seed) * 2 - 1) - Math.Abs(Noise3(px - E, py, tz, seed) * 2 - 1));
                norm += amp;
                amp *= 0.5; fr *= 2.13; tz *= 2.13; seed += 5387;
            }
            double kk = norm > 0 ? 1.7 / norm : 0;
            ox = vx * kk;
            oy = vy * kk;
        }

        /// <summary>Кубический безье-изинг как CSS cubic-bezier (<c>AFX.bezierEase</c>).</summary>
        public static double BezierEase(double p1x, double p1y, double p2x, double p2y, double x)
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double err = Bx(p1x, p2x, t) - x;
                if (Math.Abs(err) < 1e-5) return By(p1y, p2y, t);
                double d = 3 * (1 - t) * (1 - t) * p1x + 6 * (1 - t) * t * (p2x - p1x) + 3 * t * t * (1 - p2x);
                if (Math.Abs(d) < 1e-6) break;
                t = Clamp(t - err / d, 0, 1);
            }
            double lo = 0, hi = 1;
            for (int i = 0; i < 24; i++)
            {
                t = (lo + hi) / 2;
                if (Bx(p1x, p2x, t) < x) lo = t; else hi = t;
            }
            return By(p1y, p2y, t);
        }

        private static double Bx(double p1x, double p2x, double t) => 3 * p1x * (1 - t) * (1 - t) * t + 3 * p2x * (1 - t) * t * t + t * t * t;
        private static double By(double p1y, double p2y, double t) => 3 * p1y * (1 - t) * (1 - t) * t + 3 * p2y * (1 - t) * t * t + t * t * t;
    }
}
