using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>
    /// Эффект Arcaidia Effector в uGUI: композиция растягивается на RectTransform
    /// (Set Native Size — размер композиции в пикселях). Работает с Mask и RectMask2D.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("UI/Effects/Arcadia Effect (UI)")]
    public sealed class ArcadiaEffectGraphic : MaskableGraphic
    {
        [Tooltip("Файл эффекта из редактора Arcaidia Effector (library/*.json).")]
        [SerializeField] private TextAsset _effect;

        [SerializeField] private ArcadiaDisplayBlend _blend = ArcadiaDisplayBlend.Normal;

        [SerializeField] private ArcadiaPlaybackSettings _playback = new ArcadiaPlaybackSettings();

        [Tooltip("Кадр, который показывается в редакторе вне Play Mode, секунды.")]
        [SerializeField, Min(0f)] private float _previewTime = 0.3f;

        [SerializeField] private UnityEvent _onFinished = new UnityEvent();

        private readonly ArcadiaEffectPlayer _player = new ArcadiaEffectPlayer();
        private string _jsonOverride;
        private string _jsonKey;
        private bool _hidden;
        private int _playFrame = -1;
        private static Material _matNormal, _matAdd;

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
        public float Time => (float)_player.Time;
        public float Duration => (float)_player.Duration;
        public RenderTexture Texture => _player.Texture;
        public EffectDocument Document => _player.Data?.Document;
        public int ParticleCount => _player.Renderer?.ParticleCount ?? 0;

        public override Texture mainTexture => _player.Texture != null ? (Texture)_player.Texture : s_WhiteTexture;

        public override Material defaultMaterial
        {
            get
            {
                ref Material m = ref _matNormal;
                if (_blend == ArcadiaDisplayBlend.Additive) m = ref _matAdd;
                if (m != null) return m;
                Shader sh = Shader.Find("ArcadiaEffects/DisplayUI");
                if (sh == null) sh = Resources.Load<Shader>("ArcadiaEffects/ArcadiaDisplayUI");
                if (sh == null) return base.defaultMaterial;
                m = new Material(sh) { name = "ArcadiaDisplayUI_" + _blend, hideFlags = HideFlags.HideAndDontSave };
                m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                m.SetFloat("_DstBlend", (float)(_blend == ArcadiaDisplayBlend.Additive
                    ? UnityEngine.Rendering.BlendMode.One
                    : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
                return m;
            }
        }

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
            SetHidden(false);
            _player.Render();
        }

        public void Pause() => _player.Pause();
        public void Resume() => _player.Resume();

        public void Stop()
        {
            _player.Stop();
            SetHidden(true);
        }

        public void SetTime(float t)
        {
            if (!EnsureLoaded()) return;
            _player.Seek(t);
            SetHidden(false);
            _player.Render();
        }

        /// <summary>Размер RectTransform = размер композиции в пикселях.</summary>
        public override void SetNativeSize()
        {
            var doc = Document;
            if (doc == null) return;
            rectTransform.anchorMax = rectTransform.anchorMin;
            rectTransform.sizeDelta = new Vector2((float)doc.CompW, (float)doc.CompH);
            SetAllDirty();
        }

        private void Reload()
        {
            _player.Unload();
            if (!isActiveAndEnabled) return;
            StartFromEnable();
        }

        private void StartFromEnable()
        {
            if (!EnsureLoaded()) { SetHidden(true); return; }
            if (Application.isPlaying)
            {
                if (_playback.playOnEnable) Play();
                else SetHidden(true);
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
            Texture before = _player.Texture;
            if (!_player.Load(key, json, _playback)) return false;
            if (before != _player.Texture) SetMaterialDirty();
            return true;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            StartFromEnable();
        }

        protected override void OnDisable()
        {
            _player.Unload();
            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            _player.Dispose();
            base.OnDestroy();
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (this == null || !isActiveAndEnabled) return;
                if (EnsureLoaded() && !Application.isPlaying) SetTime(_previewTime);
                SetMaterialDirty();
            };
        }
#endif

        private void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                if (_player.IsStale && EnsureLoaded()) _player.Render();
                return;
            }
            if (_player.IsStale) EnsureLoaded();
            if (!_player.IsPlaying) return;
            if (_playFrame == UnityEngine.Time.frameCount) return;
            float dt = _playback.unscaledTime ? UnityEngine.Time.unscaledDeltaTime : UnityEngine.Time.deltaTime;
            bool finished = _player.Advance(dt * _playback.speed, _playback.loop);
            _player.Render();
            if (finished) Finish();
        }

        internal void EditorAdvance(float dt)
        {
            if (!EnsureLoaded()) return;
            if (!_player.IsPlaying) _player.Play(_player.IsFinished ? 0 : _player.Time);
            _player.Advance(dt * _playback.speed, true);
            SetHidden(false);
            _player.Render();
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
                    SetHidden(true);
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

        private void SetHidden(bool hidden)
        {
            if (_hidden == hidden) return;
            _hidden = hidden;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_hidden || _player.Texture == null) return;
            Rect r = GetPixelAdjustedRect();
            Color32 c = color;
            vh.AddVert(new Vector3(r.xMin, r.yMin), c, new Vector2(0, 0));
            vh.AddVert(new Vector3(r.xMin, r.yMax), c, new Vector2(0, 1));
            vh.AddVert(new Vector3(r.xMax, r.yMax), c, new Vector2(1, 1));
            vh.AddVert(new Vector3(r.xMax, r.yMin), c, new Vector2(1, 0));
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }
    }
}
