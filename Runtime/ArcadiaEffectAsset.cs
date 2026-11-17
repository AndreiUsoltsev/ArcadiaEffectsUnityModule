using System;
using UnityEngine;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Эффект, импортированный в проект (файл <c>.afx</c> — тот же JSON из library/ редактора).
    /// Импортёр заранее декодирует текстуры (премультиплицированные, со сжатием под платформу)
    /// и запекает атласы процедурных фигур, поэтому в игре нет base64, декодирования PNG
    /// и растеризации фигур — только разбор небольшого JSON документа.
    /// </summary>
    public sealed class ArcadiaEffectAsset : ScriptableObject
    {
        [Serializable]
        public sealed class TextureEntry
        {
            public string id;
            public Texture2D texture;
        }

        [Serializable]
        public sealed class SheetEntry
        {
            public string key;
            public int variants = 1;
            public Texture2D texture;
        }

        [SerializeField, HideInInspector] private string _documentJson;
        [SerializeField] private TextureEntry[] _textures = Array.Empty<TextureEntry>();
        [SerializeField] private SheetEntry[] _shapeSheets = Array.Empty<SheetEntry>();
        [SerializeField] private string[] _missingTextures = Array.Empty<string>();
        [SerializeField] private Vector2 _size;
        [SerializeField] private float _duration;

        /// <summary>JSON документа без пейлоадов текстур.</summary>
        public string DocumentJson => _documentJson;
        public TextureEntry[] Textures => _textures;
        public SheetEntry[] ShapeSheets => _shapeSheets;
        /// <summary>Текстуры, которые не удалось импортировать (например, WebP).</summary>
        public string[] MissingTextures => _missingTextures;
        /// <summary>Размер композиции, пиксели.</summary>
        public Vector2 Size => _size;
        public float Duration => _duration;

        internal void Set(string json, TextureEntry[] textures, SheetEntry[] sheets, string[] missing, Vector2 size, float duration)
        {
            _documentJson = json;
            _textures = textures;
            _shapeSheets = sheets;
            _missingTextures = missing;
            _size = size;
            _duration = duration;
        }
    }
}
