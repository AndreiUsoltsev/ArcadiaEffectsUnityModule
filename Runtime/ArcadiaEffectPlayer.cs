using System;
using UnityEngine;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Что делать, когда эффект доиграл до конца (без зацикливания).</summary>
    public enum ArcadiaFinishAction
    {
        /// <summary>Оставить последний кадр.</summary>
        KeepLastFrame,
        /// <summary>Очистить картинку (объект остаётся активным).</summary>
        Hide,
        /// <summary>Выключить GameObject.</summary>
        Disable,
        /// <summary>Уничтожить GameObject.</summary>
        Destroy
    }

    /// <summary>Настройки проигрывания, общие для мирового и UI-компонентов.</summary>
    [Serializable]
    public sealed class ArcadiaPlaybackSettings
    {
        [Tooltip("Запускать эффект при включении объекта.")]
        public bool playOnEnable = true;

        [Tooltip("Зацикливать по длительности композиции.")]
        public bool loop;

        [Tooltip("Множитель скорости проигрывания.")]
        [Min(0f)] public float speed = 1f;

        [Tooltip("Время без учёта Time.timeScale.")]
        public bool unscaledTime;

        [Tooltip("Что делать по окончании (без зацикливания).")]
        public ArcadiaFinishAction onFinish = ArcadiaFinishAction.Hide;

        [Tooltip("Уровень качества (Auto — по настройкам пакета и устройству).")]
        public ArcadiaQuality quality = ArcadiaQuality.Auto;

        [Tooltip("Множитель разрешения холста поверх уровня качества (1 — как задаёт уровень).")]
        [Range(0.1f, 2f)] public float resolutionScale = 1f;
    }

    /// <summary>
    /// Логика проигрывания без привязки к способу показа: загрузка, время, когда перерисовывать.
    /// Используется компонентами <see cref="ArcadiaEffect"/> и <see cref="ArcadiaEffectGraphic"/>.
    /// </summary>
    public sealed class ArcadiaEffectPlayer : IDisposable
    {
        public ArcadiaEffectData Data { get; private set; }
        public ArcadiaEffectRenderer Renderer { get; private set; }

        /// <summary>Текущее время композиции, секунды.</summary>
        public double Time { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsFinished { get; private set; }
        public double Duration => Data != null ? Data.Document.Duration : 0;
        public RenderTexture Texture => Renderer?.Output;

        private ArcadiaQuality _quality;
        private bool _sceneDisplay;
        private int _version = -1;
        private string _key;
        private float _sinceRender;
        private bool _dirty = true;
        private float _resLevel = 1f;

        /// <summary>Подготовить рендерер под эффект. Возвращает false, если эффекта нет.</summary>
        public bool Load(string cacheKey, Func<ArcadiaEffectData> load, ArcadiaPlaybackSettings s, bool sceneDisplay)
        {
            if (cacheKey == null || load == null) { Unload(); return false; }
            ArcadiaQuality q = ArcadiaEffectsSettings.Resolve(s.quality);
            if (Renderer != null && _key == cacheKey && _quality == q && _sceneDisplay == sceneDisplay && _version == ArcadiaEffectData.Version)
                return true;
            Unload();
            try
            {
                Data = load();
            }
            catch (Exception e)
            {
                Debug.LogError("[ArcadiaEffects] Failed to load effect: " + e.Message);
                Data = null;
                return false;
            }
            if (Data == null) return false;
            _key = cacheKey;
            _quality = q;
            _sceneDisplay = sceneDisplay;
            _version = ArcadiaEffectData.Version;
            _resLevel = 1f;
            Renderer = new ArcadiaEffectRenderer(Data, q, s.resolutionScale, sceneDisplay);
            _dirty = true;
            return true;
        }

        /// <summary>Эффект устарел (переимпортирован в редакторе) — нужен Load заново.</summary>
        public bool IsStale => Renderer != null && _version != ArcadiaEffectData.Version;

        public void Unload()
        {
            if (Renderer != null)
            {
                ArcadiaEffectsSystem.Cancel(Renderer);
                Renderer.Dispose();
            }
            Renderer = null;
            Data = null;
            _key = null;
            IsPlaying = false;
        }

        public void Dispose() => Unload();

        public void Play(double from = 0)
        {
            Time = Math.Max(0, from);
            IsPlaying = true;
            IsFinished = false;
            Renderer?.ResetSimulations();
            _dirty = true;
        }

        public void Pause() => IsPlaying = false;

        public void Resume()
        {
            if (!IsFinished) IsPlaying = true;
        }

        public void Stop()
        {
            IsPlaying = false;
            IsFinished = false;
            Time = 0;
            Renderer?.ResetSimulations();
            Release();
        }

        /// <summary>Отпустить холст и очистить показ (эффект скрыт).</summary>
        public void Release()
        {
            if (Renderer == null) return;
            ArcadiaEffectsSystem.Cancel(Renderer);
            Renderer.Clear();
            Renderer.ReleaseOutput();
        }

        /// <summary>Перемотать (назад — пересимуляция с нуля, как в редакторе).</summary>
        public void Seek(double t)
        {
            Time = Math.Max(0, t);
            IsFinished = false;
            _dirty = true;
        }

        /// <summary>Продвинуть время. Возвращает true в момент окончания (без зацикливания).</summary>
        public bool Advance(double dt, bool loop)
        {
            if (!IsPlaying || Data == null) return false;
            double dur = Math.Max(1e-4, Duration);
            Time += dt;
            _sinceRender += (float)dt;
            _dirty = true;
            if (Time < dur) return false;
            if (loop)
            {
                Time %= dur;
                return false;
            }
            Time = dur;
            IsPlaying = false;
            IsFinished = true;
            return true;
        }

        /// <summary>
        /// Пора ли перерисовать кадр: время изменилось, эффект виден, и (при ограничении частоты)
        /// прошло достаточно времени. Первый кадр после Play/Seek рисуется всегда.
        /// </summary>
        public bool ShouldRender(bool visible, bool forced = false)
        {
            if (Renderer == null) return false;
            if (forced) return true;
            if (!_dirty || !visible) return false;
            float fps = Renderer.Profile.renderFps;
            return fps <= 0 || _sinceRender >= 1f / fps - 1e-4f;
        }

        /// <summary>Заказать кадр в центральной отрисовке (вне Play Mode — сразу).</summary>
        public void Submit(IArcadiaRenderClient client)
        {
            if (Renderer == null) return;
            _dirty = false;
            _sinceRender = 0;
            ArcadiaEffectsSystem.Submit(Renderer, Time, client);
        }

        /// <summary>Отрисовать кадр сейчас же (мимо центральной очереди).</summary>
        public void RenderNow(IArcadiaRenderClient client)
        {
            if (Renderer == null) return;
            _dirty = false;
            _sinceRender = 0;
            Renderer.Render(Time);
            client?.OnArcadiaRendered(Renderer);
        }

        /// <summary>
        /// Разрешение холста по размеру эффекта на экране (пиксели по большей стороне):
        /// ступени 1 / ½ / ¼ с гистерезисом, в пределах уровня качества.
        /// </summary>
        public void UpdateResolution(float screenPixels, float userScale)
        {
            if (Renderer == null || Data == null) return;
            ArcadiaQualityProfile prof = Renderer.Profile;
            float scale = prof.maxResolutionScale * Mathf.Max(0.01f, userScale);
            if (prof.autoResolution && screenPixels > 0)
            {
                float comp = (float)Math.Max(Data.Document.CompW, Data.Document.CompH);
                float need = screenPixels / Mathf.Max(1, comp);
                float level = need >= 0.75f ? 1f : need >= 0.375f ? 0.5f : 0.25f;
                // вниз — только с запасом 15%, чтобы не дёргать разрешение на границе
                if (level < _resLevel && need > _resLevel * 0.75f * 0.85f) level = _resLevel;
                _resLevel = level;
                scale = Mathf.Min(scale, Mathf.Max(level, prof.minResolutionScale));
            }
            Renderer.ResolutionScale = scale;
        }
    }
}
