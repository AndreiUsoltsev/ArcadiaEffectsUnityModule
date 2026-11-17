using System;
using System.Collections.Generic;
using UnityEngine;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Картинка для отрисовки: текстура (премультиплицированная) и прямоугольники внутри неё.</summary>
    internal struct SpriteImage
    {
        public Texture Texture;
        /// <summary>Размер «картинки» в пикселях, как <c>img.width/height</c> в редакторе.</summary>
        public float Width, Height;
        /// <summary>UV прямоугольник основного изображения: x0, yTop, x1, yBottom.</summary>
        public Vector4 Uv;
        public Vector4 GlowUv;
        public bool Valid => Texture != null;
    }

    /// <summary>Текстура эффекта из data URL (премультиплицированная, линейная — без sRGB-конвертации).</summary>
    public sealed class EffectTexture
    {
        public Texture2D Texture;
        public TextureInfo Info;
        public int Width => Texture != null ? Texture.width : 0;
        public int Height => Texture != null ? Texture.height : 0;
    }

    /// <summary>
    /// Загруженный эффект: документ + текстуры. Неизменяем и разделяется всеми экземплярами,
    /// проигрывающими один и тот же файл.
    /// </summary>
    public sealed class ArcadiaEffectData : IDisposable
    {
        public EffectDocument Document { get; }
        private readonly Dictionary<string, EffectTexture> _textures = new Dictionary<string, EffectTexture>();
        private readonly Dictionary<string, ShapeAtlas.Sheet> _sheets = new Dictionary<string, ShapeAtlas.Sheet>();
        private readonly bool _ownsTextures = true;

        private static readonly Dictionary<string, ArcadiaEffectData> Cache = new Dictionary<string, ArcadiaEffectData>();

        private ArcadiaEffectData(EffectDocument doc)
        {
            Document = doc;
            foreach (var info in doc.Textures)
            {
                if (info.Id == null || !doc.TexturePayloads.TryGetValue(info.Id, out string url)) continue;
                Texture2D tex = DecodeDataUrl(url, doc.Name + "/" + info.Name);
                if (tex != null) _textures[info.Id] = new EffectTexture { Texture = tex, Info = info };
            }
            // пейлоады больше не нужны — это мегабайты base64
            doc.TexturePayloads.Clear();
        }

        /// <summary>Из импортированного ассета: текстуры и атласы фигур уже готовы, декодировать нечего.</summary>
        private ArcadiaEffectData(ArcadiaEffectAsset asset)
        {
            _ownsTextures = false;
            Document = EffectDocument.Parse(asset.DocumentJson);
            foreach (var e in asset.Textures)
            {
                if (e == null || e.texture == null) continue;
                TextureInfo info = Document.FindTexture(e.id);
                if (info != null) _textures[e.id] = new EffectTexture { Texture = e.texture, Info = info };
            }
            foreach (var sh in asset.ShapeSheets)
                if (sh != null && sh.texture != null)
                    _sheets[sh.key] = new ShapeAtlas.Sheet { Texture = sh.texture, Variants = Math.Max(1, sh.variants) };
            Document.TexturePayloads.Clear();
        }

        /// <summary>Разобрать JSON эффекта (файл из library/ редактора).</summary>
        public static ArcadiaEffectData Parse(string json) => new ArcadiaEffectData(EffectDocument.Parse(json));

        /// <summary>Загрузить импортированный эффект (<see cref="ArcadiaEffectAsset"/>), с кэшированием.</summary>
        public static ArcadiaEffectData FromAsset(ArcadiaEffectAsset asset)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            string key = ArcadiaEffectData.KeyOf("afx", asset);
            if (Cache.TryGetValue(key, out var d) && d.Document != null) return d;
            d = new ArcadiaEffectData(asset);
            Cache[key] = d;
            return d;
        }

        /// <summary>Готовый (запечённый импортёром) атлас фигуры, если он есть.</summary>
        internal ShapeAtlas.Sheet FindSheet(SpriteRef r, double glowBlur)
        {
            if (_sheets.Count == 0) return null;
            return _sheets.TryGetValue(ShapeAtlas.KeyOf(r, glowBlur), out var s) ? s : null;
        }

        /// <summary>Загрузить с кэшированием по ключу (например, instanceID ассета).</summary>
        public static ArcadiaEffectData Get(string key, Func<string> json)
        {
            if (Cache.TryGetValue(key, out var d) && d.Document != null) return d;
            d = Parse(json());
            Cache[key] = d;
            return d;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UnityEngine.Object, string> Keys =
            new System.Runtime.CompilerServices.ConditionalWeakTable<UnityEngine.Object, string>();
        private static int _keyCounter;

        /// <summary>
        /// Ключ кэша для ассета: по ссылке на объект, без GetInstanceID (в Unity 6.5 он запрещён),
        /// поэтому одинаково работает во всех версиях Unity.
        /// </summary>
        public static string KeyOf(string prefix, UnityEngine.Object asset)
        {
            if (!Keys.TryGetValue(asset, out string id))
            {
                id = (++_keyCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Keys.Add(asset, id);
            }
            return prefix + ":" + id;
        }

        /// <summary>Растёт при каждом сбросе кэша — плееры по нему понимают, что пора перезагрузиться.</summary>
        public static int Version { get; private set; }

        /// <summary>Сбросить кэш (например, после переимпорта файла эффекта в редакторе).</summary>
        public static void Invalidate(string key)
        {
            if (Cache.TryGetValue(key, out var d))
            {
                Cache.Remove(key);
                d.Dispose();
                Version++;
            }
        }

        /// <summary>Сбросить весь кэш эффектов.</summary>
        public static void InvalidateAll()
        {
            foreach (var d in Cache.Values) d.Dispose();
            Cache.Clear();
            Version++;
        }

        public EffectTexture GetTexture(string id) => id != null && _textures.TryGetValue(id, out var t) ? t : null;

        public void Dispose()
        {
            if (_ownsTextures)
                foreach (var t in _textures.Values) if (t.Texture != null) MeshPool.Destroy(t.Texture);
            _textures.Clear();
            _sheets.Clear();
        }

        private static readonly HashSet<string> Warned = new HashSet<string>();

        internal static Texture2D DecodeDataUrl(string url, string name)
        {
            int comma = url.IndexOf(',');
            if (!url.StartsWith("data:", StringComparison.Ordinal) || comma < 0) return null;
            string header = url.Substring(5, comma - 5);
            if (header.IndexOf("image/webp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (Warned.Add(name))
                    Debug.LogError("[ArcadiaEffects] Texture '" + name + "' is WebP, Unity cannot decode it. " +
                                   "Convert the effect with Tools~/ConvertWebpToPng.html (see README).");
                return null;
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(url.Substring(comma + 1));
            }
            catch (FormatException)
            {
                Debug.LogError("[ArcadiaEffects] Texture '" + name + "' has broken base64 payload.");
                return null;
            }
            Texture2D tex = DecodeImage(bytes, name, false);
            if (tex == null) Debug.LogError("[ArcadiaEffects] Texture '" + name + "' could not be decoded (" + header + ").");
            return tex;
        }

        /// <summary>Байты data URL (null — не base64 или WebP; mime — тип картинки).</summary>
        internal static byte[] DataUrlBytes(string url, out string mime)
        {
            mime = null;
            int comma = url.IndexOf(',');
            if (!url.StartsWith("data:", StringComparison.Ordinal) || comma < 0) return null;
            string header = url.Substring(5, comma - 5);
            int semi = header.IndexOf(';');
            mime = semi >= 0 ? header.Substring(0, semi) : header;
            try
            {
                return Convert.FromBase64String(url.Substring(comma + 1));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>PNG/JPG -> премультиплицированная линейная RGBA32-текстура.</summary>
        internal static Texture2D DecodeImage(byte[] bytes, string name, bool readable)
        {
            var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!tmp.LoadImage(bytes, false))
            {
                MeshPool.Destroy(tmp);
                return null;
            }
            // премультипликация: Canvas2D фильтрует и смешивает премультиплицированные цвета
            Color32[] px = tmp.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                if (c.a == 255) continue;
                px[i] = new Color32((byte)((c.r * c.a + 127) / 255), (byte)((c.g * c.a + 127) / 255), (byte)((c.b * c.a + 127) / 255), c.a);
            }
            var tex = new Texture2D(tmp.width, tmp.height, TextureFormat.RGBA32, false, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
            tex.SetPixels32(px);
            tex.Apply(false, !readable);
            MeshPool.Destroy(tmp);
            return tex;
        }
    }

    /// <summary>
    /// Атласы процедурных фигур: на фигуру (с параметрами и квантованным блюром свечения) —
    /// одна текстура, в строке на вариант: база 128x128 и свечение 192x192 рядом.
    /// Общие для всех эффектов, строятся лениво.
    /// </summary>
    internal static class ShapeAtlas
    {
        private const int Pad = 2;
        private const int GlowW = 192;
        private const int CellW = ShapeRasterizer.Base + Pad * 2 + GlowW + Pad * 2;
        private const int RowH = GlowW + Pad * 2;

        /// <summary>Атлас одной фигуры: строка на вариант.</summary>
        internal sealed class Sheet
        {
            public Texture2D Texture;
            public int Variants;
            private SpriteImage[] _images;

            public SpriteImage Image(int variant)
            {
                int v = Variants > 1 ? (variant < 0 ? 0 : (variant >= Variants ? Variants - 1 : variant)) : 0;
                if (_images == null)
                {
                    _images = new SpriteImage[Variants];
                    float tw = Texture.width, th = Texture.height;
                    for (int i = 0; i < Variants; i++)
                    {
                        float rowTop = i * RowH;
                        _images[i] = new SpriteImage
                        {
                            Texture = Texture,
                            Width = ShapeRasterizer.Base,
                            Height = ShapeRasterizer.Base,
                            Uv = new Vector4(Pad / tw, 1 - (rowTop + Pad) / th, (Pad + ShapeRasterizer.Base) / tw, 1 - (rowTop + Pad + ShapeRasterizer.Base) / th),
                            GlowUv = new Vector4((Pad * 3 + ShapeRasterizer.Base) / tw, 1 - (rowTop + Pad) / th,
                                (Pad * 3 + ShapeRasterizer.Base + GlowW) / tw, 1 - (rowTop + Pad + GlowW) / th)
                        };
                    }
                }
                return _images[v];
            }
        }

        private static readonly Dictionary<string, Sheet> Map = new Dictionary<string, Sheet>();
        private static Texture2D _white;

        /// <summary>Белая 4x4 — для заливки лент трейлов.</summary>
        public static Texture2D White
        {
            get
            {
                if (_white != null) return _white;
                _white = new Texture2D(4, 4, TextureFormat.RGBA32, false, true)
                {
                    name = "ArcadiaWhite", hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point
                };
                var px = new Color32[16];
                for (int i = 0; i < 16; i++) px[i] = new Color32(255, 255, 255, 255);
                _white.SetPixels32(px);
                _white.Apply(false, true);
                return _white;
            }
        }

        public static double QuantizeBlur(double blurFrac) => Js.Round(Js.Clamp(blurFrac, 0.02, 0.6) * 40) / 40;

        /// <summary>Ключ атласа: фигура + параметры + квантованный блюр свечения.</summary>
        public static string KeyOf(SpriteRef r, double glowBlur) => Key(r, QuantizeBlur(glowBlur));

        private static string Key(SpriteRef r, double bq)
        {
            return r.ShapeId + "|" + r.Th + "|" + r.N + "|" + r.Inr + "|" + r.Taper + "|" + bq.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Атлас фигуры (база + свечение всех вариантов). Кэшировать на стороне вызова.</summary>
        public static Sheet GetSheet(SpriteRef r, double glowBlur)
        {
            double bq = QuantizeBlur(glowBlur);
            string key = Key(r, bq);
            if (!Map.TryGetValue(key, out Sheet e) || e.Texture == null)
            {
                e = Build(r, bq);
                Map[key] = e;
            }
            return e;
        }

        private static Sheet Build(SpriteRef r, double bq)
        {
            byte[] a = BuildAlpha(r, bq, out int w, out int h, out int variants);
            var tex = new Texture2D(w, h, TextureFormat.Alpha8, false, true)
            {
                name = "ArcadiaShape_" + r.ShapeId,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixelData(a, 0);
            tex.Apply(false, true);
            return new Sheet { Texture = tex, Variants = variants };
        }

        /// <summary>
        /// Альфа атласа фигуры (Alpha8, строки снизу вверх — как в Texture2D). Фигуры всегда
        /// тонируются, цвет текстуры не нужен, поэтому один канал — вчетверо меньше памяти.
        /// </summary>
        public static byte[] BuildAlpha(SpriteRef r, double bq, out int w, out int h, out int variants)
        {
            variants = ShapeLibraryInfo.VariantsOf(r.ShapeId);
            w = CellW;
            h = RowH * variants;
            var px = new byte[w * h];
            for (int v = 0; v < variants; v++)
            {
                float[] baseA = ShapeRasterizer.Draw(r.ShapeId, r, v);
                float[] glowA = ShapeRasterizer.Glow(baseA, ShapeRasterizer.Base, ShapeRasterizer.Base, bq);
                int rowTop = v * RowH;
                Blit(px, w, h, baseA, ShapeRasterizer.Base, ShapeRasterizer.Base, Pad, rowTop + Pad);
                Blit(px, w, h, glowA, GlowW, GlowW, Pad * 3 + ShapeRasterizer.Base, rowTop + Pad);
            }
            return px;
        }

        // a — альфа сверху вниз; в Texture2D строка 0 — низ, поэтому переворачиваем
        private static void Blit(byte[] dst, int dw, int dh, float[] a, int aw, int ah, int x0, int yTop)
        {
            for (int y = 0; y < ah; y++)
            {
                int ty = dh - 1 - (yTop + y);
                for (int x = 0; x < aw; x++)
                {
                    dst[ty * dw + x0 + x] = (byte)Mathf.Clamp(Mathf.RoundToInt(a[y * aw + x] * 255f), 0, 255);
                }
            }
        }
    }
}
