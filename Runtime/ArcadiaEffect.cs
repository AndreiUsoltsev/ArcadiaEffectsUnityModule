using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Как эффект ложится на сцену.</summary>
    public enum ArcadiaDisplayBlend
    {
        /// <summary>Обычное наложение с прозрачностью (премультиплицированная альфа) — как в редакторе.</summary>
        Normal,
        /// <summary>Аддитивно: тёмное прозрачно, яркое светит.</summary>
        Additive
    }

    /// <summary>
    /// Эффект Arcaidia Effector в мире. Та же симуляция и тот же рендер, что в редакторе, без
    /// покадрового атласа. Центр композиции = позиция объекта, размер = пиксели / Pixels Per Unit.
    /// Слои, которым нужен холст, идут через RenderTexture; на уровнях качества с прямыми слоями
    /// остальные рисуются сразу в сцену (меш с сабмешами), без RenderTexture.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Effects/Arcadia Effect")]
    public sealed class ArcadiaEffect : MonoBehaviour, IArcadiaRenderClient
    {
        [Tooltip("Импортированный эффект (.afx). Предпочтительный способ: текстуры и атласы готовы заранее.")]
        [SerializeField] private ArcadiaEffectAsset _asset;

        [Tooltip("Файл эффекта как JSON (library/*.json) — запасной путь, всё готовится при загрузке.")]
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
        private MeshRenderer _renderer;
        private MeshFilter _filter;
        private MaterialPropertyBlock _mpb;
        private Material[] _assignedMats;
        private bool _visible;
        private int _playFrame = -1;
        private static Material _matNormal, _matAdd;
        private static readonly Dictionary<int, Material[]> MatArrays = new Dictionary<int, Material[]>();
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int CompId = Shader.PropertyToID("_Comp");

        /// <summary>Импортированный эффект (.afx). Смена перезагружает эффект.</summary>
        public ArcadiaEffectAsset Asset
        {
            get => _asset;
            set
            {
                if (_asset == value && _jsonOverride == null) return;
                _asset = value;
                _jsonOverride = null;
                Reload();
            }
        }

        /// <summary>Файл эффекта как JSON (TextAsset). Используется, если не задан <see cref="Asset"/>.</summary>
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
        /// <summary>Холст композиции (null — эффект целиком рисуется прямо в сцену или скрыт).</summary>
        public RenderTexture Texture => _player.Texture;
        public EffectDocument Document => _player.Data?.Document;
        public ArcadiaEffectRenderer Renderer => _player.Renderer;
        public int ParticleCount => _player.Renderer?.ParticleCount ?? 0;

        public float PixelsPerUnit
        {
            get => _pixelsPerUnit;
            set { _pixelsPerUnit = Mathf.Max(0.01f, value); ApplyDisplay(); }
        }

        public Color Tint
        {
            get => _tint;
            set { _tint = value; ApplyDisplay(); }
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
            _player.Submit(this);
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
            _player.Submit(this);
        }

        private void Reload()
        {
            _player.Unload();
            if (!isActiveAndEnabled) return;
            StartFromEnable();
        }

        private void StartFromEnable()
        {
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
            System.Func<ArcadiaEffectData> load;
            if (_jsonOverride != null)
            {
                string j = _jsonOverride;
                key = _jsonKey;
                load = () => ArcadiaEffectData.Get(key, () => j);
            }
            else if (_asset != null)
            {
                ArcadiaEffectAsset a = _asset;
                key = ArcadiaEffectData.KeyOf("afx", a);
                load = () => ArcadiaEffectData.FromAsset(a);
            }
            else if (_effect != null)
            {
                TextAsset a = _effect;
                key = ArcadiaEffectData.KeyOf("asset", a);
                load = () => ArcadiaEffectData.Get(key, () => a.text);
            }
            else return false;
            if (!_player.Load(key, load, _playback, true)) return false;
            _player.Renderer.DisplayPixelsPerUnit = _pixelsPerUnit;
            return true;
        }

        private void OnEnable()
        {
            _renderer = GetComponent<MeshRenderer>();
            _filter = GetComponent<MeshFilter>();
            _assignedMats = null;
            StartFromEnable();
        }

        private void OnDisable()
        {
            _player.Unload();
            if (_filter != null) _filter.sharedMesh = null;
        }

        private void OnDestroy() => _player.Dispose();

#if UNITY_EDITOR
        private void OnValidate()
        {
            _pixelsPerUnit = Mathf.Max(0.01f, _pixelsPerUnit);
            // в OnValidate нельзя трогать рендереры и меши — откладываем
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || !isActiveAndEnabled) return;
                if (EnsureLoaded() && !Application.isPlaying) SetTime(_previewTime);
                else ApplyDisplay();
            };
        }
#endif

        private void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                if (_player.IsStale && EnsureLoaded()) SetTime(_previewTime);
                return;
            }
            if (_player.IsStale && EnsureLoaded()) _player.Seek(_player.Time);
            if (_player.Renderer == null) return;

            bool justStarted = _playFrame == UnityEngine.Time.frameCount;
            if (_player.IsPlaying && !justStarted)
            {
                float dt = _playback.unscaledTime ? UnityEngine.Time.unscaledDeltaTime : UnityEngine.Time.deltaTime;
                if (_player.Advance(dt * _playback.speed, _playback.loop))
                {
                    _player.Submit(this);
                    Finish();
                    return;
                }
            }
            UpdateResolution();
            // невидимый эффект не рисуем: симуляция догонит время при следующем показе
            bool visible = _visible && _renderer != null && _renderer.isVisible;
            if (_player.ShouldRender(visible)) _player.Submit(this);
        }

        /// <summary>Размер на экране по главной камере -> разрешение холста.</summary>
        private void UpdateResolution()
        {
            var doc = _player.Data?.Document;
            Camera cam = Camera.main;
            if (doc == null || cam == null) { _player.UpdateResolution(0, _playback.resolutionScale); return; }
            float worldSize = (float)System.Math.Max(doc.CompW, doc.CompH) / _pixelsPerUnit;
            Vector3 s = transform.lossyScale;
            worldSize *= Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
            float pxPerUnit;
            if (cam.orthographic) pxPerUnit = cam.pixelHeight / (2f * cam.orthographicSize);
            else
            {
                float dist = Vector3.Dot(transform.position - cam.transform.position, cam.transform.forward);
                if (dist <= cam.nearClipPlane) { _player.UpdateResolution(0, _playback.resolutionScale); return; }
                pxPerUnit = cam.pixelHeight / (2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
            }
            _player.UpdateResolution(worldSize * pxPerUnit, _playback.resolutionScale);
        }

        /// <summary>Продвинуть время вручную (редакторский предпросмотр).</summary>
        internal void EditorAdvance(float dt)
        {
            if (!EnsureLoaded()) return;
            if (!_player.IsPlaying) _player.Play(_player.IsFinished ? 0 : _player.Time);
            _player.Advance(dt * _playback.speed, true);
            SetVisible(true);
            _player.Submit(this);
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
                    _player.Release();
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

        void IArcadiaRenderClient.OnArcadiaRendered(ArcadiaEffectRenderer renderer)
        {
            if (this == null || renderer != _player.Renderer) return;
            ApplyDisplay();
        }

        private void SetVisible(bool on)
        {
            _visible = on;
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            if (_renderer != null) _renderer.enabled = on && _player.Renderer != null;
        }

        /// <summary>Меш показа, материалы по сабмешам и их текстуры.</summary>
        private void ApplyDisplay()
        {
            ArcadiaEffectRenderer r = _player.Renderer;
            if (r == null) return;
            if (_renderer == null) _renderer = GetComponent<MeshRenderer>();
            if (_filter == null) _filter = GetComponent<MeshFilter>();
            r.DisplayPixelsPerUnit = _pixelsPerUnit;
            if (_filter.sharedMesh != r.DisplayMesh) _filter.sharedMesh = r.DisplayMesh;

            int n = r.DisplaySubMeshCount;
            Material m = SharedMaterial(_blend);
            if (m == null) return;
            int key = n * 2 + (int)_blend;
            if (!MatArrays.TryGetValue(key, out Material[] arr) || arr[0] != m)
            {
                arr = new Material[Mathf.Max(1, n)];
                for (int i = 0; i < arr.Length; i++) arr[i] = m;
                MatArrays[key] = arr;
            }
            // массив общий для всех эффектов с тем же числом сабмешей (sharedMaterials-геттер аллоцирует — не читаем)
            if (!ReferenceEquals(_assignedMats, arr))
            {
                _renderer.sharedMaterials = arr;
                _assignedMats = arr;
            }

            if (_mpb == null) _mpb = new MaterialPropertyBlock();
            var doc = r.Data.Document;
            var comp = new Vector4((float)doc.CompW, (float)doc.CompH, 1f / _pixelsPerUnit, 0);
            for (int i = 0; i < n; i++)
            {
                _mpb.Clear();
                Texture t = r.DisplayTexture(i);
                if (t != null) _mpb.SetTexture(Materials.MainTex, t);
                _mpb.SetVector(CompId, comp);
                _mpb.SetColor(ColorId, _tint);
                _renderer.SetPropertyBlock(_mpb, i);
            }
            _renderer.enabled = _visible && n > 0;
        }

        private static Material SharedMaterial(ArcadiaDisplayBlend blend)
        {
            ref Material m = ref _matNormal;
            if (blend == ArcadiaDisplayBlend.Additive) m = ref _matAdd;
            if (m != null) return m;
            Shader sh = Resources.Load<Shader>("ArcadiaEffects/ArcadiaDisplay");
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
