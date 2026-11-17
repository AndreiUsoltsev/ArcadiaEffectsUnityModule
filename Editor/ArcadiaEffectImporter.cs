using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace InteractiveSoftware.ArcadiaEffects.Editor
{
    /// <summary>
    /// Импорт эффекта Arcaidia Effector (<c>.afx</c> — тот же JSON, что library/*.json) в
    /// <see cref="ArcadiaEffectAsset"/>: текстуры декодируются и премультиплицируются заранее
    /// (со сжатием под платформу сборки), атласы процедурных фигур запекаются. В игре остаётся
    /// только разобрать небольшой JSON документа.
    /// </summary>
    [ScriptedImporter(3, "afx")]
    public sealed class ArcadiaEffectImporter : ScriptedImporter
    {
        public enum TextureCompression
        {
            /// <summary>ПК — без сжатия (один в один с редактором), мобильные и WebGL — со сжатием.</summary>
            Automatic,
            /// <summary>Без сжатия на всех платформах (RGBA32).</summary>
            None,
            /// <summary>Сжимать на всех платформах.</summary>
            Compressed
        }

        [Tooltip("Сжатие текстур эффекта.")]
        public TextureCompression textureCompression = TextureCompression.Automatic;

        [Tooltip("Качество сжатия (дольше импорт — лучше картинка).")]
        public UnityEditor.TextureCompressionQuality compressionQuality = UnityEditor.TextureCompressionQuality.Normal;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string text = File.ReadAllText(ctx.assetPath);
            object rootObj;
            EffectDocument doc;
            try
            {
                rootObj = Json.Parse(text);
                doc = EffectDocument.Parse(text);
            }
            catch (System.Exception e)
            {
                ctx.LogImportError("Arcadia effect is not valid JSON: " + e.Message);
                return;
            }
            var root = rootObj as Dictionary<string, object>;
            var docObj = root != null && root.TryGetValue("doc", out object d) && d is Dictionary<string, object> dd ? dd : root;
            Dictionary<string, object> tex = root != null && root.TryGetValue("tex", out object t) ? t as Dictionary<string, object> : null;

            BuildTarget target = ctx.selectedBuildTarget;
            bool compress = textureCompression == TextureCompression.Compressed ||
                            (textureCompression == TextureCompression.Automatic && IsMobileOrWeb(target));

            // --- текстуры ---
            var textures = new List<ArcadiaEffectAsset.TextureEntry>();
            var missing = new List<string>();
            foreach (TextureInfo info in doc.Textures)
            {
                if (info.Id == null) continue;
                string label = string.IsNullOrEmpty(info.Name) ? info.Id : info.Name;
                if (tex == null || !tex.TryGetValue(info.Id, out object payload) || !(payload is string url))
                {
                    missing.Add(label + " (no payload)");
                    continue;
                }
                byte[] bytes = ArcadiaEffectData.DataUrlBytes(url, out string mime);
                if (bytes == null || (mime != null && mime.IndexOf("webp", System.StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    missing.Add(label + (bytes == null ? " (broken payload)" : " (WebP)"));
                    continue;
                }
                Texture2D t2 = ArcadiaEffectData.DecodeImage(bytes, label, true);
                if (t2 == null)
                {
                    missing.Add(label + " (" + mime + " not decodable)");
                    continue;
                }
                t2.name = label;
                if (compress) Compress(t2, target);
                t2.Apply(false, true);
                ctx.AddObjectToAsset("tex_" + info.Id, t2);
                textures.Add(new ArcadiaEffectAsset.TextureEntry { id = info.Id, texture = t2 });
            }
            foreach (string m in missing)
                ctx.LogImportWarning("Texture '" + m + "' is skipped. WebP must be converted with Tools~/ConvertWebpToPng.html.");

            // --- атласы фигур: только то, что эффект реально использует ---
            var sheets = new List<ArcadiaEffectAsset.SheetEntry>();
            var seen = new HashSet<string>();
            void AddShape(SpriteRef r, double glowBlur)
            {
                if (r == null || r.IsTexture) return;
                string key = ShapeAtlas.KeyOf(r, glowBlur);
                if (!seen.Add(key)) return;
                byte[] a = ShapeAtlas.BuildAlpha(r, ShapeAtlas.QuantizeBlur(glowBlur), out int w, out int h, out int variants);
                var st = new Texture2D(w, h, TextureFormat.Alpha8, false, true)
                {
                    name = "shape " + r.ShapeId,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                st.SetPixelData(a, 0);
                st.Apply(false, true);
                ctx.AddObjectToAsset("shape_" + Hash128.Compute(key), st);
                sheets.Add(new ArcadiaEffectAsset.SheetEntry { key = key, variants = variants, texture = st });
            }
            foreach (Layer l in doc.Layers)
            {
                if (l.Type == LayerType.Emitter)
                {
                    AddShape(l.Pt.Sprite, l.Pt.GlowBlur);
                    if (l.Em.Sub.On) AddShape(l.Em.Sub.Pt.Sprite, l.Em.Sub.Pt.GlowBlur);
                }
                else if ((l.Type == LayerType.Sprite || l.Type == LayerType.Atlas) && l.Sp != null)
                {
                    AddShape(l.Sp.Sprite, l.Sp.GlowBlur);
                }
            }

            // --- документ без мегабайтов base64 ---
            var slim = new Dictionary<string, object> { { "app", "arcadia-effects" }, { "doc", docObj } };
            var asset = ScriptableObject.CreateInstance<ArcadiaEffectAsset>();
            asset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            asset.Set(Json.Serialize(slim), textures.ToArray(), sheets.ToArray(), missing.ToArray(),
                new Vector2((float)doc.CompW, (float)doc.CompH), (float)doc.Duration);
            ctx.AddObjectToAsset("effect", asset);
            ctx.SetMainObject(asset);
        }

        private static bool IsMobileOrWeb(BuildTarget t) =>
            t == BuildTarget.Android || t == BuildTarget.iOS || t == BuildTarget.WebGL || t == BuildTarget.tvOS;

        private void Compress(Texture2D t, BuildTarget target)
        {
            TextureFormat fmt;
            if (target == BuildTarget.Android || target == BuildTarget.iOS || target == BuildTarget.tvOS)
                fmt = TextureFormat.ASTC_6x6;
            else if (t.width % 4 == 0 && t.height % 4 == 0)
                fmt = TextureFormat.DXT5;
            else
                return; // DXT требует размеров, кратных 4 — оставляем без сжатия
            EditorUtility.CompressTexture(t, fmt, compressionQuality);
        }
    }

    internal static class ArcadiaImportMenu
    {
        [MenuItem("Assets/Arcadia Effects/Convert JSON to .afx", true)]
        private static bool CanConvert()
        {
            foreach (Object o in Selection.objects)
                if (o is TextAsset && AssetDatabase.GetAssetPath(o).EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Переименовать выбранные .json эффекты в .afx (GUID сохраняется).</summary>
        [MenuItem("Assets/Arcadia Effects/Convert JSON to .afx")]
        private static void Convert()
        {
            foreach (Object o in Selection.objects)
            {
                string path = AssetDatabase.GetAssetPath(o);
                if (!(o is TextAsset) || !path.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)) continue;
                string dst = Path.ChangeExtension(path, ".afx");
                string err = AssetDatabase.MoveAsset(path, dst);
                if (!string.IsNullOrEmpty(err)) Debug.LogError("[ArcadiaEffects] " + err);
            }
        }

        /// <summary>Скопировать эффект из library/ редактора в выбранную папку проекта как .afx.</summary>
        [MenuItem("Assets/Arcadia Effects/Import from Arcaidia library...")]
        private static void ImportFromLibrary()
        {
            string src = EditorUtility.OpenFilePanel("Arcaidia effect", "", "json");
            if (string.IsNullOrEmpty(src)) return;
            string folder = "Assets";
            if (Selection.activeObject != null)
            {
                string p = AssetDatabase.GetAssetPath(Selection.activeObject);
                if (AssetDatabase.IsValidFolder(p)) folder = p;
                else if (!string.IsNullOrEmpty(p)) folder = Path.GetDirectoryName(p)?.Replace('\\', '/') ?? "Assets";
            }
            string dst = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileNameWithoutExtension(src) + ".afx");
            File.Copy(src, dst);
            AssetDatabase.ImportAsset(dst);
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<ArcadiaEffectAsset>(dst);
        }
    }
}
