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

    public enum ArcadiaAntiAliasing
    {
        Off = 1,
        X2 = 2,
        X4 = 4,
        X8 = 8
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

        [Tooltip("Масштаб RenderTexture относительно размера композиции (1 = пиксель в пиксель, как в редакторе).")]
        [Range(0.25f, 2f)] public float resolutionScale = 1f;

        [Tooltip("Сглаживание лент трейлов и молний.")]
        public ArcadiaAntiAliasing antiAliasing = ArcadiaAntiAliasing.X4;
    }

    /// <summary>
    /// Логика проигрывания без привязки к способу показа: загрузка, время, рендер кадра.
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

        private float _scale;
        private int _msaa;
        private int _version = -1;
        private string _key;

        /// <summary>Подготовить рендерер под эффект. Возвращает false, если эффекта нет.</summary>
        public bool Load(string cacheKey, Func<string> json, ArcadiaPlaybackSettings s)
        {
            if (cacheKey == null || json == null) { Unload(); return false; }
            float scale = s.resolutionScale;
            int msaa = (int)s.antiAliasing;
            if (Renderer != null && _key == cacheKey && Mathf.Approximately(_scale, scale) && _msaa == msaa && _version == ArcadiaEffectData.Version)
                return true;
            Unload();
            try
            {
                Data = ArcadiaEffectData.Get(cacheKey, json);
            }
            catch (Exception e)
            {
                Debug.LogError("[ArcadiaEffects] Failed to load effect: " + e.Message);
                Data = null;
                return false;
            }
            _key = cacheKey;
            _scale = scale;
            _msaa = msaa;
            _version = ArcadiaEffectData.Version;
            Renderer = new ArcadiaEffectRenderer(Data, scale, msaa);
            return true;
        }

        /// <summary>Эффект устарел (переимпортирован в редакторе) — нужен Load заново.</summary>
        public bool IsStale => Renderer != null && _version != ArcadiaEffectData.Version;

        public void Unload()
        {
            Renderer?.Dispose();
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
            Renderer?.Clear();
        }

        /// <summary>Перемотать (назад — пересимуляция с нуля, как в редакторе).</summary>
        public void Seek(double t)
        {
            Time = Math.Max(0, t);
            IsFinished = false;
        }

        /// <summary>Продвинуть время. Возвращает true в момент окончания (без зацикливания).</summary>
        public bool Advance(double dt, bool loop)
        {
            if (!IsPlaying || Data == null) return false;
            double dur = Math.Max(1e-4, Duration);
            Time += dt;
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

        /// <summary>Отрисовать текущий кадр.</summary>
        public void Render()
        {
            Renderer?.Render(Time);
        }
    }
}
