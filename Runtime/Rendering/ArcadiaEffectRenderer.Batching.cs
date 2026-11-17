using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteractiveSoftware.ArcadiaEffects
{
    // Цели отрисовки, пакеты и трафаретные фигуры.
    public sealed partial class ArcadiaEffectRenderer
    {
        private RenderTexture _curTarget;
        private double _curW, _curH;
        private Texture _bTex;
        private DrawBlend _bBlend;
        private bool _bDepth;
        private int _strokeK;

        /// <summary>Куда сейчас пишется геометрия: меш кадра (холст) или меш показа (прямые слои).</summary>
        private GeometryBuffer CurGeo => _toDirect ? _direct : _frame;

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

        /// <summary>Режим вершины: тонировка и (в прямых слоях) аддитивность через альфу 0.</summary>
        private float ModeFor(bool tint, DrawBlend blend) =>
            (tint ? VtxMode.Tint : VtxMode.Tex) + (_toDirect && blend == DrawBlend.Add ? VtxMode.Add : 0);

        /// <summary>
        /// Открыть пакет под текстуру и наложение. На холсте пакет рвут смена текстуры или режима;
        /// в прямых слоях normal и add уживаются в одном пакете (add = вывод с альфой 0).
        /// </summary>
        private void BeginBatch(Texture tex, DrawBlend blend, bool depth = false)
        {
            if (_toDirect)
            {
                if (_bTex != tex) CloseDirect();
                _bTex = tex;
                return;
            }
            if (_bTex != tex || _bBlend != blend || _bDepth != depth) FlushBatch();
            _bTex = tex;
            _bBlend = blend;
            _bDepth = depth;
        }

        private void BeginTris(Texture tex, DrawBlend blend) => BeginBatch(tex, blend);

        /// <summary>Глубина следующего штриха: каждый следующий ближе предыдущего.</summary>
        private float NextStrokeDepth()
        {
            _strokeK++;
            return Mathf.Max(0f, 1f - _strokeK * 2.5e-7f);
        }

        private void AddQuad(Texture tex, DrawBlend blend, Affine m, float x0, float y0, float x1, float y1, Vector4 uv, Vector4 col, bool tint)
        {
            BeginBatch(tex, blend);
            CurGeo.AddQuad(m.Apply(x0, y0), m.Apply(x1, y0), m.Apply(x1, y1), m.Apply(x0, y1), uv, ModeFor(tint, blend), col);
        }

        /// <summary>Закрыть пакет холста и записать его отрисовку.</summary>
        private void FlushBatch()
        {
            if (_toDirect) { CloseDirect(); return; }
            int sm = _frame.Close();
            if (sm >= 0)
            {
                _mpb.Clear();
                _mpb.SetTexture(Materials.MainTex, _bTex);
                StencilMode st = _bDepth ? StencilMode.DepthStroke : StencilMode.None;
                StencilMode last = _bDepth && _bBlend == DrawBlend.Multiply ? StencilMode.DepthStrokeEqual : st;
                DrawMesh(_frame.Mesh, sm, _bBlend, st, last);
            }
            _bTex = null;
            _bDepth = false;
        }

        /// <summary>Закрыть пакет прямых слоёв (сабмеш меша показа).</summary>
        private void CloseDirect()
        {
            int sm = _direct.Close();
            if (sm >= 0) _directTextures.Add(_bTex);
            _bTex = null;
        }

        private void DrawMesh(Mesh mesh, int subMesh, DrawBlend blend, StencilMode st, StencilMode stLast)
        {
            if (blend == DrawBlend.Multiply)
            {
                _cmd.DrawMesh(mesh, Matrix4x4.identity, Materials.Draw(3, st), subMesh, 0, _mpb);
                _cmd.DrawMesh(mesh, Matrix4x4.identity, Materials.Draw(4, stLast), subMesh, 0, _mpb);
                return;
            }
            int b = blend == DrawBlend.Add ? 1 : blend == DrawBlend.Screen ? 2 : blend == DrawBlend.Copy ? 5 : 0;
            _cmd.DrawMesh(mesh, Matrix4x4.identity, Materials.Draw(b, stLast), subMesh, 0, _mpb);
        }

        /// <summary>Нарисовать холст (премультиплицированный) поверх цели на весь кадр композиции.</summary>
        private void DrawFull(RenderTexture target, Texture src, DrawBlend blend, double alpha)
        {
            Bind(target, _cw, _ch);
            AddQuad(src, blend, Affine.Identity, 0, 0, (float)_cw, (float)_ch, new Vector4(0, 1, 1, 0), new Vector4(1, 1, 1, (float)alpha), false);
        }

        private RenderTexture CopyOf(RenderTexture src)
        {
            RenderTexture c = Temp(false, src.width, src.height, 1);
            BindClear(c, _cw, _ch);
            DrawFull(c, src, DrawBlend.Copy, 1);
            return c;
        }

        // ---------- штрихи трейлов на холсте: объединение через глубину ----------
        // Canvas2D закрашивает контур штриха один раз, сколько бы его куски ни перекрывались.
        // Здесь то же даёт буфер глубины: у каждого штриха своя глубина, повторные пиксели того же
        // штриха отбрасываются тестом, следующий штрих ближе и проходит. Все штрихи пакета —
        // один вызов отрисовки (раньше — трафарет и два вызова на штрих).

        /// <summary>Круг (или его дуга) веером вокруг центра.</summary>
        private static void Fan(GeometryBuffer g, float cx, float cy, float r, float a0, float a1, int seg, in HalfColor col, float z)
        {
            int c = g.AddVertex(cx, cy, 0.5f, 0.5f, VtxMode.Tint, col, z);
            for (int s = 0; s <= seg; s++)
            {
                float a = a0 + (a1 - a0) * s / seg;
                g.AddVertex(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r, 0.5f, 0.5f, VtxMode.Tint, col, z);
            }
            for (int s = 0; s < seg; s++) g.AddTri(c, c + 1 + s, c + 2 + s);
        }

        private int DiskSegments(float r) => Mathf.Clamp(Mathf.CeilToInt(r * _scale * 0.75f) + 6, 8, 48);

        /// <summary>Штрих ломаной с круглыми стыками и концами (lineJoin/lineCap round).</summary>
        private void StrokeUnion(List<Vector2> pts, double width, Vector3 rgb, double alpha, DrawBlend blend)
        {
            if (pts.Count == 0 || alpha <= 0) return;
            BeginBatch(ShapeAtlas.White, blend, true);
            GeometryBuffer g = _frame;
            float z = NextStrokeDepth();
            HalfColor col = HalfColor.Pack(new Vector4(rgb.x, rgb.y, rgb.z, (float)alpha));
            float h = (float)(width * 0.5);
            List<Vector2> _dec = pts; // точки уже прорежены при сборе пути
            int n = _dec.Count;
            int seg = DiskSegments(h);
            const float twoPi = Mathf.PI * 2;
            // круглые концы
            Fan(g, _dec[0].x, _dec[0].y, h, 0, twoPi, seg, col, z);
            if (n > 1) Fan(g, _dec[n - 1].x, _dec[n - 1].y, h, 0, twoPi, seg, col, z);
            for (int i = 1; i < n; i++)
            {
                Vector2 q = _dec[i - 1], p = _dec[i];
                Vector2 d = p - q;
                float l = d.magnitude;
                if (l < 1e-5f) continue;
                Vector2 nn = new Vector2(-d.y, d.x) * (h / l);
                int b = g.AddVertex(q.x + nn.x, q.y + nn.y, 0.5f, 0.5f, VtxMode.Tint, col, z);
                g.AddVertex(p.x + nn.x, p.y + nn.y, 0.5f, 0.5f, VtxMode.Tint, col, z);
                g.AddVertex(p.x - nn.x, p.y - nn.y, 0.5f, 0.5f, VtxMode.Tint, col, z);
                g.AddVertex(q.x - nn.x, q.y - nn.y, 0.5f, 0.5f, VtxMode.Tint, col, z);
                g.AddTri(b, b + 1, b + 2);
                g.AddTri(b, b + 2, b + 3);
                // круглый стык — только где излом заметен (зазор между прямоугольниками > ~0.3 px)
                if (i < n - 1)
                {
                    Vector2 d2 = _dec[i + 1] - p;
                    float l2 = d2.magnitude;
                    if (l2 < 1e-5f) continue;
                    float cos = Vector2.Dot(d, d2) / (l * l2);
                    float ang = Mathf.Acos(Mathf.Clamp(cos, -1f, 1f));
                    if (h * _scale * ang > 0.3f)
                        Fan(g, p.x, p.y, h, 0, twoPi, Mathf.Max(6, Mathf.CeilToInt(seg * ang / Mathf.PI) * 2 + 2), col, z);
                }
            }
        }

        /// <summary>Лента с переменной шириной: полоса между кромками + полукруглые капы (как ribbonPath).</summary>
        private void RibbonUnion(double scale, bool core, Vector3 rgb, double alpha, DrawBlend blend)
        {
            if (alpha <= 0 || _rn < 2) return;
            BeginBatch(ShapeAtlas.White, blend, true);
            GeometryBuffer g = _frame;
            float z = NextStrokeDepth();
            HalfColor col = HalfColor.Pack(new Vector4(rgb.x, rgb.y, rgb.z, (float)alpha));
            int prev = -1;
            for (int i = 0; i < _rn; i++)
            {
                double h = RibbonHw(i, scale, core);
                int a = g.AddVertex((float)(_rx[i] + _rnx[i] * h), (float)(_ry[i] + _rny[i] * h), 0.5f, 0.5f, VtxMode.Tint, col, z);
                g.AddVertex((float)(_rx[i] - _rnx[i] * h), (float)(_ry[i] - _rny[i] * h), 0.5f, 0.5f, VtxMode.Tint, col, z);
                if (prev >= 0)
                {
                    g.AddTri(prev, prev + 1, a + 1);
                    g.AddTri(prev, a + 1, a);
                }
                prev = a;
            }
            int n = _rn;
            float hh = (float)RibbonHw(n - 1, scale, core), ht = (float)RibbonHw(0, scale, core);
            float aH = (float)Math.Atan2(_rny[n - 1], _rnx[n - 1]);
            float aT = (float)Math.Atan2(_rny[0], _rnx[0]);
            Fan(g, (float)_rx[n - 1], (float)_ry[n - 1], hh, aH, aH - Mathf.PI, Mathf.Max(6, DiskSegments(hh) / 2), col, z);
            Fan(g, (float)_rx[0], (float)_ry[0], ht, aT - Mathf.PI, aT - 2 * Mathf.PI, Mathf.Max(6, DiskSegments(ht) / 2), col, z);
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
    }
}
