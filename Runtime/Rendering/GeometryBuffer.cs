using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Вершина: позиция (px композиции), uv + режим, цвет (half×4). 32 байта.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Vtx
    {
        public float X, Y, Z;
        public float U, V, M;
        public ushort R, G, B, A;

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 4)
        };
    }

    /// <summary>Цвет вершины, уже упакованный в half — упаковка один раз на примитив, а не на вершину.</summary>
    internal struct HalfColor
    {
        public ushort R, G, B, A;

        public static HalfColor Pack(Vector4 c) => new HalfColor
        {
            R = Half.From(c.x), G = Half.From(c.y), B = Half.From(c.z), A = Half.From(c.w)
        };
    }

    /// <summary>float -> half (IEEE 754, округление к ближайшему) без вызовов движка.</summary>
    internal static class Half
    {
        [StructLayout(LayoutKind.Explicit)]
        private struct Bits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public uint U;
        }

        public static ushort From(float f)
        {
            uint x = new Bits { F = f }.U;
            uint sign = (x >> 16) & 0x8000u;
            int exp = (int)((x >> 23) & 0xFF) - 127 + 15;
            uint mant = x & 0x7FFFFFu;
            if (exp >= 31) return (ushort)(sign | 0x7C00u);            // переполнение -> бесконечность
            if (exp <= 0)
            {
                if (exp < -10) return (ushort)sign;                     // в ноль
                mant |= 0x800000u;                                     // денормализованное
                int shift = 14 - exp;
                uint h = mant >> shift;
                if (((mant >> (shift - 1)) & 1u) != 0) h++;
                return (ushort)(sign | h);
            }
            uint r = sign | ((uint)exp << 10) | (mant >> 13);
            if ((mant & 0x1000u) != 0) r++;                            // округление
            return (ushort)r;
        }
    }

    /// <summary>
    /// Режим вершины (uv.z): 0 — премультиплицированная текстура, 1 — тонировка (source-in),
    /// +2 — аддитивно (только для прямых слоёв: в сцене сложение = вывод с альфой 0).
    /// </summary>
    internal static class VtxMode
    {
        public const float Tex = 0, Tint = 1, Add = 2;
    }

    /// <summary>
    /// Геометрия кадра одного меша: вершины, индексы и сабмеши (пакеты). Сабмеш получает номер
    /// в момент закрытия пакета, данные заливаются в меш одним вызовом в конце кадра.
    /// </summary>
    internal sealed class GeometryBuffer : IDisposable
    {
        public readonly Mesh Mesh;
        private Vtx[] _v = new Vtx[1024];
        private int[] _i = new int[1536];
        private SubMeshDescriptor[] _sub = new SubMeshDescriptor[16];
        private int _vc, _ic, _sc, _open;
        private int _vCap, _iCap;

        public int VertexCount => _vc;
        public int SubMeshCount => _sc;
        /// <summary>В открытом пакете есть индексы.</summary>
        public bool HasOpen => _ic > _open;

        public GeometryBuffer(string name)
        {
            Mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            Mesh.MarkDynamic();
        }

        public void Reset()
        {
            _vc = 0;
            _ic = 0;
            _sc = 0;
            _open = 0;
        }

        private void EnsureV(int add)
        {
            if (_vc + add <= _v.Length) return;
            Array.Resize(ref _v, Math.Max(_v.Length * 2, _vc + add));
        }

        private void EnsureI(int add)
        {
            if (_ic + add <= _i.Length) return;
            Array.Resize(ref _i, Math.Max(_i.Length * 2, _ic + add));
        }

        public int AddVertex(float x, float y, float u, float v, float mode, Vector4 c, float z = 0) =>
            AddVertex(x, y, u, v, mode, HalfColor.Pack(c), z);

        public int AddVertex(float x, float y, float u, float v, float mode, in HalfColor c, float z = 0)
        {
            EnsureV(1);
            ref Vtx t = ref _v[_vc];
            t.X = x; t.Y = y; t.Z = z;
            t.U = u; t.V = v; t.M = mode;
            t.R = c.R; t.G = c.G; t.B = c.B; t.A = c.A;
            return _vc++;
        }

        public void AddTri(int a, int b, int c)
        {
            EnsureI(3);
            _i[_ic++] = a;
            _i[_ic++] = b;
            _i[_ic++] = c;
        }

        public void AddQuad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector4 uv, float mode, Vector4 col)
        {
            HalfColor c = HalfColor.Pack(col);
            int b = AddVertex(p0.x, p0.y, uv.x, uv.y, mode, c);
            AddVertex(p1.x, p1.y, uv.z, uv.y, mode, c);
            AddVertex(p2.x, p2.y, uv.z, uv.w, mode, c);
            AddVertex(p3.x, p3.y, uv.x, uv.w, mode, c);
            AddTri(b, b + 1, b + 2);
            AddTri(b, b + 2, b + 3);
        }

        /// <summary>Закрыть текущий пакет. Возвращает номер сабмеша (-1 — пустой пакет).</summary>
        public int Close()
        {
            if (_ic == _open) return -1;
            if (_sc >= _sub.Length) Array.Resize(ref _sub, _sub.Length * 2);
            _sub[_sc] = new SubMeshDescriptor(_open, _ic - _open, MeshTopology.Triangles);
            _open = _ic;
            int n = _sc++;
            // номер сабмеша проверяется уже при записи команды — число сабмешей растёт сразу
            if (n >= _subCap) SetSubMeshCount(Math.Max(n + 1, _subCap * 2), default, true);
            return n;
        }

        private int _subCap = 1;
        private SubMeshDescriptor[] _desc = new SubMeshDescriptor[16];

        private const MeshUpdateFlags Flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices |
                                              MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontResetBoneBounds;

        /// <summary>
        /// Число сабмешей и их описания одним вызовом SetSubMeshes: сеттер subMeshCount
        /// перекраивает индексный буфер, а SetSubMeshes его не трогает.
        /// </summary>
        /// <param name="empty">
        /// Только завести сабмеши (посреди записи кадра): данных этого кадра в буфере ещё нет,
        /// поэтому описания пустые — настоящие диапазоны ставит <see cref="Upload"/>.
        /// </param>
        private void SetSubMeshCount(int count, Bounds bounds, bool empty = false)
        {
            if (_desc.Length < count) Array.Resize(ref _desc, Math.Max(count, _desc.Length * 2));
            for (int s = 0; s < count; s++)
            {
                SubMeshDescriptor d = !empty && s < _sc ? _sub[s] : new SubMeshDescriptor(0, 0, MeshTopology.Triangles);
                // с DontRecalculateBounds Unity не считает диапазон вершин и границы сабмеша сам
                d.firstVertex = 0;
                d.vertexCount = empty ? 0 : _vc;
                d.bounds = bounds;
                _desc[s] = d;
            }
            Mesh.SetSubMeshes(_desc, 0, count, Flags);
            _subCap = count;
        }

        /// <summary>
        /// Залить данные кадра в меш. <paramref name="exactSubMeshes"/> — ровно столько сабмешей,
        /// сколько пакетов (меш показа в сцене); иначе сабмеши остаются с запасом (меш кадра).
        /// </summary>
        public void Upload(Bounds bounds, bool exactSubMeshes)
        {
            int vNeed = Math.Max(4, _vc), iNeed = Math.Max(6, _ic);
            if (vNeed > _vCap)
            {
                _vCap = Math.Max(vNeed, _vCap * 2);
                Mesh.SetVertexBufferParams(_vCap, Vtx.Layout);
            }
            if (iNeed > _iCap)
            {
                _iCap = Math.Max(iNeed, _iCap * 2);
                Mesh.SetIndexBufferParams(_iCap, IndexFormat.UInt32);
            }
            if (_vc > 0) Mesh.SetVertexBufferData(_v, 0, 0, _vc, 0, Flags);
            if (_ic > 0) Mesh.SetIndexBufferData(_i, 0, 0, _ic, Flags);
            int want = exactSubMeshes ? Math.Max(1, _sc) : Math.Max(_subCap, Math.Max(1, _sc));
            SetSubMeshCount(want, bounds);
            Mesh.bounds = bounds;
        }

        public void Dispose() => MeshPool.Destroy(Mesh);
    }
}
