using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Аффинная матрица Canvas2D: x' = A*x + C*y + E, y' = B*x + D*y + F.</summary>
    internal struct Affine
    {
        public double A, B, C, D, E, F;

        public static readonly Affine Identity = new Affine { A = 1, D = 1 };

        public static Affine Translate(double x, double y) => new Affine { A = 1, D = 1, E = x, F = y };

        public static Affine Scale(double sx, double sy) => new Affine { A = sx, D = sy };

        public static Affine Rotate(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Affine { A = c, B = s, C = -s, D = c };
        }

        /// <summary>translate(px,py) * scale(1,q) * rotate(a) * scale(sx,sy) — <c>setMat</c> движка.</summary>
        public static Affine Particle(double px, double py, double a, double sx, double sy, double q)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Affine { A = c * sx, B = s * sx * q, C = -s * sy, D = c * sy * q, E = px, F = py };
        }

        /// <summary>this * n (сначала применяется n).</summary>
        public Affine Mul(Affine n)
        {
            return new Affine
            {
                A = A * n.A + C * n.B,
                B = B * n.A + D * n.B,
                C = A * n.C + C * n.D,
                D = B * n.C + D * n.D,
                E = A * n.E + C * n.F + E,
                F = B * n.E + D * n.F + F
            };
        }

        public Vector3 Apply(double x, double y) => new Vector3((float)(A * x + C * y + E), (float)(B * x + D * y + F), 0);
    }

    internal enum DrawBlend { Normal, Add, Screen, Multiply, Copy }

    internal enum StencilMode { None, WriteUnion, WriteNonzero, CoverKeep, CoverZero }

    /// <summary>Кэш материалов: шейдер один, состояние смешивания/трафарета — свойствами материала.</summary>
    internal static class Materials
    {
        private static Shader _draw, _post;
        private static readonly Dictionary<int, Material> DrawMats = new Dictionary<int, Material>();
        private static Material _postMat;

        public static readonly int MainTex = Shader.PropertyToID("_MainTex");
        public static readonly int Proj = Shader.PropertyToID("_AfxProj");

        public static bool Ready
        {
            get
            {
                if (_draw == null) _draw = Resources.Load<Shader>("ArcadiaEffects/ArcadiaDraw");
                if (_post == null) _post = Resources.Load<Shader>("ArcadiaEffects/ArcadiaPost");
                return _draw != null && _post != null && _draw.isSupported && _post.isSupported;
            }
        }

        public static Material Post
        {
            get
            {
                if (_postMat == null) _postMat = new Material(_post) { hideFlags = HideFlags.HideAndDontSave };
                return _postMat;
            }
        }

        // 0 Normal, 1 Add, 2 Screen, 3 MulA, 4 MulB, 5 Copy
        public static Material Draw(int blend, StencilMode st)
        {
            int key = blend * 16 + (int)st;
            if (DrawMats.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(_draw) { hideFlags = HideFlags.HideAndDontSave, name = "ArcadiaDraw_" + key };
            BlendMode sc, dc, sa, da;
            switch (blend)
            {
                case 1: sc = BlendMode.One; dc = BlendMode.One; sa = BlendMode.One; da = BlendMode.One; break;
                case 2: sc = BlendMode.One; dc = BlendMode.OneMinusSrcColor; sa = BlendMode.One; da = BlendMode.OneMinusSrcAlpha; break;
                // multiply = Cs*(1-ad) + Cd*(1-as) + Cs*Cd, точно в два прохода
                case 3: sc = BlendMode.DstColor; dc = BlendMode.OneMinusSrcAlpha; sa = BlendMode.Zero; da = BlendMode.One; break;
                case 4: sc = BlendMode.OneMinusDstAlpha; dc = BlendMode.One; sa = BlendMode.One; da = BlendMode.OneMinusSrcAlpha; break;
                case 5: sc = BlendMode.One; dc = BlendMode.Zero; sa = BlendMode.One; da = BlendMode.Zero; break;
                default: sc = BlendMode.One; dc = BlendMode.OneMinusSrcAlpha; sa = BlendMode.One; da = BlendMode.OneMinusSrcAlpha; break;
            }
            m.SetFloat("_SrcC", (float)sc);
            m.SetFloat("_DstC", (float)dc);
            m.SetFloat("_SrcA", (float)sa);
            m.SetFloat("_DstA", (float)da);
            m.SetFloat("_ColorMask", 15);
            m.SetFloat("_StencilRef", 0);
            m.SetFloat("_StencilCompF", (float)CompareFunction.Always);
            m.SetFloat("_StencilPassF", (float)StencilOp.Keep);
            m.SetFloat("_StencilCompB", (float)CompareFunction.Always);
            m.SetFloat("_StencilPassB", (float)StencilOp.Keep);
            switch (st)
            {
                case StencilMode.WriteUnion:
                    m.SetFloat("_ColorMask", 0);
                    m.SetFloat("_StencilRef", 1);
                    m.SetFloat("_StencilPassF", (float)StencilOp.Replace);
                    m.SetFloat("_StencilPassB", (float)StencilOp.Replace);
                    break;
                case StencilMode.WriteNonzero:
                    m.SetFloat("_ColorMask", 0);
                    m.SetFloat("_StencilPassF", (float)StencilOp.IncrementWrap);
                    m.SetFloat("_StencilPassB", (float)StencilOp.DecrementWrap);
                    break;
                case StencilMode.CoverKeep:
                case StencilMode.CoverZero:
                    var op = st == StencilMode.CoverZero ? StencilOp.Zero : StencilOp.Keep;
                    m.SetFloat("_StencilCompF", (float)CompareFunction.NotEqual);
                    m.SetFloat("_StencilCompB", (float)CompareFunction.NotEqual);
                    m.SetFloat("_StencilPassF", (float)op);
                    m.SetFloat("_StencilPassB", (float)op);
                    break;
            }
            DrawMats[key] = m;
            return m;
        }
    }

    /// <summary>Пул динамических мешей: у каждого пакета отрисовки кадра свой меш.</summary>
    internal sealed class MeshPool : IDisposable
    {
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private int _next;

        public void Reset() => _next = 0;

        public Mesh Next()
        {
            if (_next >= _meshes.Count)
            {
                var m = new Mesh { name = "ArcadiaBatch", hideFlags = HideFlags.HideAndDontSave, indexFormat = IndexFormat.UInt32 };
                m.MarkDynamic();
                _meshes.Add(m);
            }
            Mesh mesh = _meshes[_next++];
            mesh.Clear();
            return mesh;
        }

        /// <summary>Отпустить лишние меши после «пикового» кадра.</summary>
        public void Trim(int keep)
        {
            for (int i = _meshes.Count - 1; i >= Math.Max(keep, _next); i--)
            {
                Destroy(_meshes[i]);
                _meshes.RemoveAt(i);
            }
        }

        public void Dispose()
        {
            foreach (var m in _meshes) Destroy(m);
            _meshes.Clear();
        }

        internal static void Destroy(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
