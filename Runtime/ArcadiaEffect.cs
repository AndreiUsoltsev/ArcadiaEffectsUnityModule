using UnityEngine;
using UnityEngine.Events;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Как композиция ложится на сцену.</summary>
    public enum ArcadiaDisplayBlend
    {
        /// <summary>Обычное наложение с прозрачностью (премультиплицированная альфа) — как в редакторе.</summary>
        Normal,
        /// <summary>Аддитивно: тёмное прозрачно, яркое светит.</summary>
        Additive
    }

    /// <summary>
    /// Эффект Arcaidia Effector в мире: квад с композицией, которую каждый кадр рисует
    /// <see cref="ArcadiaEffectRenderer"/> — та же симуляция и тот же рендер, что в редакторе,
    /// без покадрового атласа. Центр композиции = позиция объекта.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Effects/Arcadia Effect")]
    public sealed class ArcadiaEffect : MonoBehaviour
    {
        [Tooltip("Файл эффекта из редактора Arcaidia Effector (library/*.json).")]
        [SerializeField] private TextAsset _effect;

        [Tooltip("Пикселей композиции на юнит мира.")]
        [SerializeField, Min(0.01f)] private float _pixelsPerUnit = 100f;

        [SerializeField] private ArcadiaDisplayBlend _blend = ArcadiaDisplayBlend.Normal;

        [Tooltip("Множитель цвета и прозрачности.")]
        [SerializeField] private Color _tint = Color.white;

        [SerializeField] private ArcadiaPlaybackSettings _playback = new ArcadiaPlaybackSettings();

        [Tooltip("Кадр, который показывается в редакторе вне Play Mode, секунды.")]
        [SerializeField, Min(0f)] private float _previewTime = 0.3f;

        [SerializeField] private UnityEvent _onFinished = new UnityEvent();

        private readonly ArcadiaEffectPlayer _player = new ArcadiaEffectPlayer();
        private string _jsonOverride;
        private string _jsonKey;
        private Mesh _mesh;
        private MaterialPropertyBlock _mpb;
        private MeshRenderer _renderer;
        private Vector2 _meshSize;
        private int _playFrame = -1;
        private static Material _matNormal, _matAdd;
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        /// <summary>Файл эффекта. Смена перезагружает эффект.</summary>
        public TextAsset Effect
        {
            get => _effect;
            set
            {
                if (_effect == value && _jsonOverride == null) return;
                _effect = value;
                _jsonOverride = null;
                Reload();
            }
        }

        public ArcadiaPlaybackSettings Playback => _playback;
        public UnityEvent OnFinished => _onFinished;
        public bool IsPlaying => _player.IsPlaying;
        /// <summary>Текущее время композиции, секунды.</summary>
        public float Time => (float)_player.Time;
        /// <summary>Длительность композиции, секунды.</summary>
        public float Duration => (float)_player.Duration;
        /// <summary>Готовая композиция (обновляется каждый кадр).</summary>
        public RenderTexture Texture => _player.Texture;
        public EffectDocument Document => _player.Data?.Document;
        public int ParticleCount => _player.Renderer?.ParticleCount ?? 0;

        public float PixelsPerUnit
        {
            get => _pixelsPerUnit;
            set { _pixelsPerUnit = Mathf.Max(0.01f, value); UpdateMesh(); }
        }

        public Color Tint
        {
            get => _tint;
            set { _tint = value; ApplyMaterial(); }
        }

        /// <summary>Загрузить эффект из JSON-строки (например, прочитанной из файла).</summary>
        public void LoadJson(string json)
        {
            _jsonOverride = json;
            _jsonKey = "json:" + json.Length + ":" + json.GetHashCode();
            Reload();
        }

        public void Play() => Play(0f);

        public void Play(float fromTime)
        {
            if (!EnsureLoaded()) return;
            _player.Play(fromTime);
            _playFrame = UnityEngine.Time.frameCount;
            SetVisible(true);
            RenderNow();
        }

        public void Pause() => _player.Pause();
        public void Resume() => _player.Resume();

        public void Stop()
        {
            _player.Stop();
            SetVisible(false);
        }

        /// <summary>Показать кадр в момент t (без запуска).</summary>
        public void SetTime(float t)
        {
            if (!EnsureLoaded()) return;
            _player.Seek(t);
            SetVisible(true);
            RenderNow();
        }

        private void Reload()
        {
            _player.Unload();
            if (!isActiveAndEnabled) return;
            if (!EnsureLoaded()) { SetVisible(false); return; }
            if (Application.isPlaying)
            {
                if (_playback.playOnEnable) Play();
                else SetVisible(false);
            }
            else SetTime(_previewTime);
        }

        private bool EnsureLoaded()
        {
            string key;
            System.Func<string> json;
            if (_jsonOverride != null)
            {
                string j = _jsonOverride;
                key = _jsonKey;
                json = () => j;
            }
            else if (_effect != null)
            {
                TextAsset a = _effect;
                key = "asset:" + a.GetInstanceID();
                json = () => a.text;
            }
            else return false;
            if (!_player.Load(key, json, _playback)) return false;
            UpdateMesh();
            ApplyMaterial();
            return true;
        }

        private void OnEnable()
        {
            _renderer = GetComponent<MeshRenderer>();
            if (!EnsureLoaded()) { SetVisible(false); return; }
            if (Application.isPlaying)
            {
                if (_playback.playOnEnable) Play();
                else SetVisible(false);
            }
            else SetTime(_previewTime);
        }

        private void OnDisable()
        {
            _player.Unload();
        }

        private void OnDestroy()
        {
            _player.Dispose();
            if (_mesh != null) MeshPool.Destroy(_mesh);
        }

        private void OnValidate()
        {
            _pixelsPerUnit = Mathf.Max(0.01f, _pixelsPerUnit);
#if UNITY_EDITOR
            // в OnValidate нельзя трогать рендереры и меши — откладываем
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || !isActiveAndEnabled) return;
                // в инспекторе могли поменять файл, масштаб или сглаживание
                if (EnsureLoaded() && !Application.isPlaying) SetTime(_previewTime);
                else if (_player.Renderer != null) ApplyMaterial();
            };
#endif
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                if (_player.IsStale && EnsureLoaded()) RenderNow();
                return;
            }
            if (_player.IsStale) EnsureLoaded();
            if (!_player.IsPlaying) return;
            if (_playFrame == UnityEngine.Time.frameCount) return; // кадр t=from уже нарисован в Play
            float dt = _playback.unscaledTime ? UnityEngine.Time.unscaledDeltaTime : UnityEngine.Time.deltaTime;
            bool finished = _player.Advance(dt * _playback.speed, _playback.loop);
            RenderNow();
            if (finished) Finish();
        }

        /// <summary>Продвинуть время вручную (редакторский предпросмотр).</summary>
        internal void EditorAdvance(float dt)
        {
            if (!EnsureLoaded()) return;
            if (!_player.IsPlaying) _player.Play(_player.IsFinished ? 0 : _player.Time);
            _player.Advance(dt * _playback.speed, true);
            SetVisible(true);
            RenderNow();
        }

        internal float PreviewTime
        {
            get => _previewTime;
            set => _previewTime = Mathf.Max(0, value);
        }

        private void Finish()
        {
            switch (_playback.onFinish)
            {
                case ArcadiaFinishAction.Hide:
                    _player.Renderer?.Clear();
                    SetVisible(false);
                    break;
                case ArcadiaFinishAction.Disable:
                    gameObject.SetActive(false);
                    break;
                case ArcadiaFinishAction.Destroy:
                    Destroy(gameObject);
                    break;
            }
            _onFinished.Invoke();
        }

        private void RenderNow()
        {
            _player.Render();
        }

        private void SetVisible(bool on)
        {
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            if (_renderer != null) _renderer.enabled = on && _player.Renderer != null;
        }

        private void UpdateMesh()
        {
            var doc = _player.Data?.Document;
            if (doc == null) return;
            var size = new Vector2((float)doc.CompW / _pixelsPerUnit, (float)doc.CompH / _pixelsPerUnit);
            if (_mesh != null && size == _meshSize) return;
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "ArcadiaEffectQuad", hideFlags = HideFlags.HideAndDontSave };
            }
            _meshSize = size;
            float hx = size.x / 2, hy = size.y / 2;
            _mesh.Clear();
            _mesh.vertices = new[] { new Vector3(-hx, -hy), new Vector3(hx, -hy), new Vector3(hx, hy), new Vector3(-hx, hy) };
            _mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            _mesh.colors32 = new[] { new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255), new Color32(255, 255, 255, 255) };
            _mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            _mesh.RecalculateBounds();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
        }

        private void ApplyMaterial()
        {
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            if (_renderer == null) return;
            Material m = SharedMaterial(_blend);
            if (m != null && _renderer.sharedMaterial != m) _renderer.sharedMaterial = m;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_mpb);
            if (_player.Texture != null) _mpb.SetTexture(Materials.MainTex, _player.Texture);
            _mpb.SetColor(ColorId, _tint);
            _renderer.SetPropertyBlock(_mpb);
        }

        private static Material SharedMaterial(ArcadiaDisplayBlend blend)
        {
            ref Material m = ref _matNormal;
            if (blend == ArcadiaDisplayBlend.Additive) m = ref _matAdd;
            if (m != null) return m;
            Shader sh = Shader.Find("ArcadiaEffects/Display");
            if (sh == null) sh = Resources.Load<Shader>("ArcadiaEffects/ArcadiaDisplay");
            if (sh == null) return null;
            m = new Material(sh) { name = "ArcadiaDisplay_" + blend, hideFlags = HideFlags.HideAndDontSave };
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            m.SetFloat("_DstBlend", (float)(blend == ArcadiaDisplayBlend.Additive
                ? UnityEngine.Rendering.BlendMode.One
                : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            return m;
        }
    }
}
