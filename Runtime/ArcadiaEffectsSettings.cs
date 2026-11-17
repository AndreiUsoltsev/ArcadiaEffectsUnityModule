using System;
using UnityEngine;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Уровень качества. High — один в один с редактором Arcaidia Effector.</summary>
    public enum ArcadiaQuality
    {
        /// <summary>Выбрать по устройству: ПК — High, WebGL и сильные мобильные — Medium, слабые мобильные — Low.</summary>
        Auto = -1,
        Low = 0,
        Medium = 1,
        High = 2
    }

    /// <summary>Что разрешено и сколько стоит эффект на одном уровне качества.</summary>
    [Serializable]
    public sealed class ArcadiaQualityProfile
    {
        [Header("Разрешение")]
        [Tooltip("Максимальный масштаб RenderTexture относительно размера композиции.")]
        [Range(0.1f, 2f)] public float maxResolutionScale = 1f;

        [Tooltip("Минимальный масштаб при автоматическом подборе по размеру на экране.")]
        [Range(0.1f, 1f)] public float minResolutionScale = 0.5f;

        [Tooltip("Подбирать разрешение по размеру эффекта на экране (маленький эффект — маленькая текстура).")]
        public bool autoResolution = true;

        [Header("Отрисовка")]
        [Tooltip("Слои без свечения слоя, затухания и постэффектов рисовать прямо в сцену, без RenderTexture. " +
                 "В Linear-проекте смешивание таких слоёв идёт в линейном пространстве, а не в гамме, как в редакторе.")]
        public bool directLayers;

        [Tooltip("Ленты трейлов через трафарет (как в редакторе). Выкл. — простые полосы без трафарета и без MSAA.")]
        public bool stencilTrails = true;

        [Tooltip("MSAA для лент трейлов (1, 2, 4, 8). Только для эффектов с трейлами.")]
        public int trailMsaa = 4;

        [Tooltip("Перерисовок в секунду (0 — каждый кадр). Симуляция всё равно идёт шагом 1/120.")]
        [Min(0)] public float renderFps;

        [Tooltip("Максимум частиц на эмиттер (в редакторе 5000).")]
        [Min(1)] public int maxParticles = Simulation.MaxParts;

        [Header("Свечение слоя и постэффекты")]
        public bool layerGlow = true;

        [Tooltip("Блюр свечения слоя считается на уменьшенной копии, пока sigma больше этого значения (пиксели RT).")]
        [Range(1f, 12f)] public float layerGlowMaxSigma = 6f;

        public bool motionBlur = true;

        [Tooltip("Максимум сэмплов смаза (степень двойки).")]
        public int motionBlurMaxSamples = 64;

        public bool displace = true;

        [Tooltip("Во сколько раз огрубить сетку искажения (1 — как в редакторе).")]
        [Range(1, 4)] public int displaceGridMultiplier = 1;

        [Tooltip("Максимальный размер блока (пикселей RT) при выборке пиксель-арта peak/box.")]
        [Range(4, 96)] public int pixelBlockMax = 96;

        public static ArcadiaQualityProfile High() => new ArcadiaQualityProfile();

        public static ArcadiaQualityProfile Medium() => new ArcadiaQualityProfile
        {
            maxResolutionScale = 0.75f,
            minResolutionScale = 0.25f,
            directLayers = true,
            trailMsaa = 2,
            renderFps = 60,
            maxParticles = 2500,
            layerGlowMaxSigma = 3f,
            motionBlurMaxSamples = 8,
            displaceGridMultiplier = 2,
            pixelBlockMax = 48
        };

        public static ArcadiaQualityProfile Low() => new ArcadiaQualityProfile
        {
            maxResolutionScale = 0.5f,
            minResolutionScale = 0.25f,
            directLayers = true,
            stencilTrails = false,
            trailMsaa = 1,
            renderFps = 30,
            maxParticles = 1200,
            layerGlowMaxSigma = 2f,
            motionBlurMaxSamples = 4,
            displace = false,
            displaceGridMultiplier = 4,
            pixelBlockMax = 24
        };
    }

    /// <summary>
    /// Настройки пакета. Необязательный ассет <c>Resources/ArcadiaEffectsSettings</c>
    /// (Create → Arcadia Effects → Settings); без него действуют значения по умолчанию.
    /// </summary>
    [CreateAssetMenu(menuName = "Arcadia Effects/Settings", fileName = "ArcadiaEffectsSettings")]
    public sealed class ArcadiaEffectsSettings : ScriptableObject
    {
        [Tooltip("Уровень качества по умолчанию для всех эффектов.")]
        public ArcadiaQuality quality = ArcadiaQuality.Auto;

        [Tooltip("Сколько эффектов перерисовывать за кадр (0 — без ограничения). Остальные показывают прошлый кадр.")]
        [Min(0)] public int maxRendersPerFrame;

        public ArcadiaQualityProfile high = ArcadiaQualityProfile.High();
        public ArcadiaQualityProfile medium = ArcadiaQualityProfile.Medium();
        public ArcadiaQualityProfile low = ArcadiaQualityProfile.Low();

        private static ArcadiaEffectsSettings _instance;
        private static ArcadiaQuality? _override;

        public static ArcadiaEffectsSettings Instance
        {
            get
            {
                if (_instance != null) return _instance;
                _instance = Resources.Load<ArcadiaEffectsSettings>("ArcadiaEffectsSettings");
                if (_instance == null)
                {
                    _instance = CreateInstance<ArcadiaEffectsSettings>();
                    _instance.hideFlags = HideFlags.HideAndDontSave;
                }
                return _instance;
            }
        }

        /// <summary>Уровень качества, заданный в рантайме (перекрывает ассет). Null — как в ассете.</summary>
        public static ArcadiaQuality? QualityOverride
        {
            get => _override;
            set => _override = value;
        }

        /// <summary>Итоговый уровень: явный, иначе глобальный, иначе подобранный по устройству.</summary>
        public static ArcadiaQuality Resolve(ArcadiaQuality requested)
        {
            ArcadiaQuality q = requested != ArcadiaQuality.Auto ? requested : (_override ?? Instance.quality);
            return q != ArcadiaQuality.Auto ? q : DetectQuality();
        }

        public static ArcadiaQualityProfile Profile(ArcadiaQuality q)
        {
            var s = Instance;
            switch (Resolve(q))
            {
                case ArcadiaQuality.Low: return s.low ?? ArcadiaQualityProfile.Low();
                case ArcadiaQuality.Medium: return s.medium ?? ArcadiaQualityProfile.Medium();
                default: return s.high ?? ArcadiaQualityProfile.High();
            }
        }

        /// <summary>Уровень по устройству.</summary>
        public static ArcadiaQuality DetectQuality()
        {
            if (Application.platform == RuntimePlatform.WebGLPlayer) return ArcadiaQuality.Medium;
            if (Application.isMobilePlatform)
            {
                bool weak = SystemInfo.systemMemorySize < 3000 || SystemInfo.graphicsMemorySize < 1024 || SystemInfo.processorCount < 6;
                return weak ? ArcadiaQuality.Low : ArcadiaQuality.Medium;
            }
            return ArcadiaQuality.High;
        }
    }
}
