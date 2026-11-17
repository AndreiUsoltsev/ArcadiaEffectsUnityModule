using System;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Запечённый путь движения эмиттера (js/core/path.js): полилиния с накопленной длиной дуги.
    /// Свой буфер у каждой симуляции — шарить нельзя.
    /// </summary>
    internal sealed class PathBake
    {
        private const int Sub = 12;
        private const int MaxSamp = 600;

        public double[] Xs = new double[64], Ys = new double[64], Cum = new double[64];
        public int[] Si = new int[64];
        public int N;
        public double Len;
        public bool Closed;

        private double[] _cx = new double[8], _cy = new double[8];

        public struct At
        {
            public double X, Y, Tx, Ty;
        }

        public struct Near
        {
            public double X, Y, Tx, Ty, U, D;
            public int Seg, I;
        }

        private void Push(double x, double y, int seg)
        {
            if (N > 0 && Math.Abs(x - Xs[N - 1]) < 1e-4 && Math.Abs(y - Ys[N - 1]) < 1e-4) return;
            if (N >= Xs.Length)
            {
                int cap = Xs.Length * 2;
                Array.Resize(ref Xs, cap);
                Array.Resize(ref Ys, cap);
                Array.Resize(ref Cum, cap);
                Array.Resize(ref Si, cap);
            }
            Xs[N] = x; Ys[N] = y; Si[N] = seg; N++;
        }

        private static double Cr(double p0, double p1, double p2, double p3, double u)
        {
            double u2 = u * u, u3 = u2 * u;
            return 0.5 * ((2 * p1) + (-p0 + p2) * u +
                          (2 * p0 - 5 * p1 + 4 * p2 - p3) * u2 +
                          (-p0 + 3 * p1 - 3 * p2 + p3) * u3);
        }

        public void Bake(PathSettings path, double tl, double ox, double oy)
        {
            int nn = path.NodeX.Length;
            if (_cx.Length < nn) { _cx = new double[nn]; _cy = new double[nn]; }
            for (int i = 0; i < nn; i++)
            {
                _cx[i] = ox + path.NodeX[i].Eval(tl);
                _cy[i] = oy + path.NodeY[i].Eval(tl);
            }
            N = 0;
            Len = 0;
            Closed = path.Closed && nn > 2;
            if (nn == 0) return;

            if (nn == 1)
            {
                Push(_cx[0], _cy[0], 0);
            }
            else
            {
                int spans = Closed ? nn : nn - 1;
                bool smooth = path.Smooth && nn > 2;
                for (int s = 0; s < spans && N < MaxSamp; s++)
                {
                    if (!smooth) { Push(_cx[s], _cy[s], s); continue; }
                    int i0 = Idx(s - 1, nn), i1 = Idx(s, nn), i2 = Idx(s + 1, nn), i3 = Idx(s + 2, nn);
                    for (int k = 0; k < Sub && N < MaxSamp; k++)
                    {
                        double u = (double)k / Sub;
                        Push(Cr(_cx[i0], _cx[i1], _cx[i2], _cx[i3], u), Cr(_cy[i0], _cy[i1], _cy[i2], _cy[i3], u), s);
                    }
                }
                if (Closed) Push(Xs[0], Ys[0], spans - 1);
                else Push(_cx[nn - 1], _cy[nn - 1], spans - 1);
            }

            Cum[0] = 0;
            double len = 0;
            for (int i = 1; i < N; i++)
            {
                double dx = Xs[i] - Xs[i - 1], dy = Ys[i] - Ys[i - 1];
                len += Math.Sqrt(dx * dx + dy * dy);
                Cum[i] = len;
            }
            Len = len;
        }

        private int Idx(int i, int nn) => Closed ? ((i % nn) + nn) % nn : (i < 0 ? 0 : (i > nn - 1 ? nn - 1 : i));

        /// <summary>Точка по доле длины дуги u (0..1; у замкнутого — заворачивается).</summary>
        public At PointAt(double u)
        {
            At o;
            if (N <= 0) { o.X = 0; o.Y = 0; o.Tx = 1; o.Ty = 0; return o; }
            if (N == 1 || Len < 1e-6) { o.X = Xs[0]; o.Y = Ys[0]; o.Tx = 1; o.Ty = 0; return o; }
            u = Closed ? u - Math.Floor(u) : Js.Clamp(u, 0, 1);
            double d = u * Len;
            int lo = 0, hi = N - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) >> 1;
                if (Cum[mid] <= d) lo = mid; else hi = mid;
            }
            double seg = Cum[lo + 1] - Cum[lo];
            double f = seg > 1e-9 ? (d - Cum[lo]) / seg : 0;
            double dx = Xs[lo + 1] - Xs[lo], dy = Ys[lo + 1] - Ys[lo];
            double l = Math.Sqrt(dx * dx + dy * dy);
            if (l == 0) l = 1;
            o.X = Xs[lo] + dx * f;
            o.Y = Ys[lo] + dy * f;
            o.Tx = dx / l;
            o.Ty = dy / l;
            return o;
        }

        private double _nbD2, _nbF;
        private int _nbI, _nbK;

        private void ScanRange(double x, double y, int lo, int hi, int segN, bool wrap)
        {
            double best = double.PositiveInfinity, bf = 0;
            int bi = -1, bk = -1;
            for (int k = lo; k <= hi; k++)
            {
                int i = k;
                if (wrap) i = ((k % segN) + segN) % segN;
                else if (k < 0 || k >= segN) continue;
                double ax = Xs[i], ay = Ys[i];
                double dx = Xs[i + 1] - ax, dy = Ys[i + 1] - ay;
                double l2 = dx * dx + dy * dy;
                double f = l2 > 1e-12 ? ((x - ax) * dx + (y - ay) * dy) / l2 : 0;
                f = f < 0 ? 0 : (f > 1 ? 1 : f);
                double px = ax + dx * f - x, py = ay + dy * f - y;
                double dd = px * px + py * py;
                if (dd < best) { best = dd; bi = i; bf = f; bk = k; }
            }
            _nbD2 = best; _nbI = bi; _nbF = bf; _nbK = bk;
        }

        /// <summary>Ближайшая точка пути; hint/win — поиск в окне вокруг прошлого сегмента.</summary>
        public Near Nearest(double x, double y, int hint, int win)
        {
            Near o;
            if (N <= 0)
            {
                o.X = x; o.Y = y; o.Tx = 1; o.Ty = 0; o.U = 0; o.D = 0; o.Seg = 0; o.I = -1;
                return o;
            }
            if (N == 1)
            {
                o.X = Xs[0]; o.Y = Ys[0]; o.Tx = 1; o.Ty = 0; o.U = 0; o.Seg = Si[0]; o.I = 0;
                o.D = Math.Sqrt((x - o.X) * (x - o.X) + (y - o.Y) * (y - o.Y));
                return o;
            }
            int segN = N - 1;
            bool windowed = hint >= 0 && win > 0 && segN > win * 2 + 2;
            if (windowed)
            {
                ScanRange(x, y, hint - win, hint + win, segN, Closed);
                if (_nbI < 0 || _nbK == hint - win || _nbK == hint + win) windowed = false;
            }
            if (!windowed) ScanRange(x, y, 0, segN - 1, segN, false);

            int ii = _nbI;
            double ff = _nbF;
            double ax2 = Xs[ii], ay2 = Ys[ii];
            double ddx = Xs[ii + 1] - ax2, ddy = Ys[ii + 1] - ay2;
            double ll = Math.Sqrt(ddx * ddx + ddy * ddy);
            if (ll == 0) ll = 1;
            o.X = ax2 + ddx * ff;
            o.Y = ay2 + ddy * ff;
            o.Tx = ddx / ll;
            o.Ty = ddy / ll;
            o.U = Len > 1e-6 ? (Cum[ii] + ll * ff) / Len : 0;
            o.D = Math.Sqrt(_nbD2);
            o.Seg = Si[ii];
            o.I = ii;
            return o;
        }
    }
}
