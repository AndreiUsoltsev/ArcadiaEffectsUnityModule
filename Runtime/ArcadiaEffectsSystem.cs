using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Получатель уведомления «кадр эффекта отрисован».</summary>
    public interface IArcadiaRenderClient
    {
        void OnArcadiaRendered(ArcadiaEffectRenderer renderer);
    }

    /// <summary>
    /// Центральная отрисовка: все эффекты кадра записываются в один CommandBuffer и выполняются
    /// одним вызовом в конце LateUpdate. Бюджет <see cref="ArcadiaEffectsSettings.maxRendersPerFrame"/>:
    /// при перегрузке дольше всех ждавшие эффекты перерисовываются первыми, остальные показывают
    /// прошлый кадр (симуляция всё равно догонит время). Вне Play Mode кадр рисуется сразу.
    /// </summary>
    public static class ArcadiaEffectsSystem
    {
        private struct Job
        {
            public ArcadiaEffectRenderer Renderer;
            public double Time;
            public IArcadiaRenderClient Client;
        }

        private static readonly List<Job> Queue = new List<Job>();
        private static readonly List<Job> Batch = new List<Job>();
        private static CommandBuffer _cmd;
        private static ArcadiaEffectsRunner _runner;
        private static readonly ProfilerMarker MarkFlush = new ProfilerMarker("ArcadiaEffects.Flush");

        /// <summary>Сколько эффектов отрисовано на последнем кадре.</summary>
        public static int RenderedLastFrame { get; private set; }

        /// <summary>Время записи и выполнения отрисовки эффектов на последнем кадре, мс (CPU).</summary>
        public static double LastFlushMs { get; private set; }

        private static readonly System.Diagnostics.Stopwatch Watch = new System.Diagnostics.Stopwatch();

        /// <summary>Сколько эффектов ждали, но не уложились в бюджет на последнем кадре.</summary>
        public static int SkippedLastFrame { get; private set; }

        /// <summary>Заказать кадр эффекта в момент <paramref name="time"/>.</summary>
        public static void Submit(ArcadiaEffectRenderer renderer, double time, IArcadiaRenderClient client)
        {
            if (renderer == null) return;
            if (!Application.isPlaying)
            {
                renderer.Render(time);
                renderer.LastRenderedFrame = Time.frameCount;
                client?.OnArcadiaRendered(renderer);
                return;
            }
            EnsureRunner();
            for (int i = 0; i < Queue.Count; i++)
            {
                if (Queue[i].Renderer != renderer) continue;
                Queue[i] = new Job { Renderer = renderer, Time = time, Client = client };
                return;
            }
            Queue.Add(new Job { Renderer = renderer, Time = time, Client = client });
        }

        /// <summary>Отменить заказ (эффект выключен или уничтожен до конца кадра).</summary>
        public static void Cancel(ArcadiaEffectRenderer renderer)
        {
            for (int i = Queue.Count - 1; i >= 0; i--)
                if (Queue[i].Renderer == renderer) Queue.RemoveAt(i);
        }

        /// <summary>
        /// Подготовить эффект заранее, чтобы первый показ не дёргал кадр: загрузка, материалы,
        /// атласы фигур (для <see cref="ArcadiaEffectAsset"/> они уже запечены импортёром).
        /// </summary>
        public static void Prewarm(ArcadiaEffectAsset asset, ArcadiaQuality quality = ArcadiaQuality.Auto)
        {
            if (asset == null) return;
            Prewarm(ArcadiaEffectData.FromAsset(asset), quality);
        }

        public static void Prewarm(ArcadiaEffectData data, ArcadiaQuality quality = ArcadiaQuality.Auto)
        {
            if (data == null || !Materials.Ready) return;
            for (int b = 0; b <= 5; b++)
            {
                Materials.Draw(b, StencilMode.None);
                Materials.Draw(b, StencilMode.DepthStroke);
            }
            _ = Materials.Post;
            // один невидимый кадр: создаёт атласы фигур, шейдерные варианты и пул RT
            using (var r = new ArcadiaEffectRenderer(data, quality, 0.25f))
                r.Render(data.Document.Duration * 0.3);
        }

        internal static void Flush()
        {
            if (Queue.Count == 0)
            {
                RenderedLastFrame = 0;
                SkippedLastFrame = 0;
                LastFlushMs = 0;
                return;
            }
            Watch.Restart();
            using (MarkFlush.Auto())
            {
                int budget = ArcadiaEffectsSettings.Instance.maxRendersPerFrame;
                Batch.Clear();
                if (budget <= 0 || Queue.Count <= budget)
                {
                    Batch.AddRange(Queue);
                    Queue.Clear();
                }
                else
                {
                    // дольше всех ждавшие — первыми
                    Queue.Sort((a, b) => a.Renderer.LastRenderedFrame.CompareTo(b.Renderer.LastRenderedFrame));
                    for (int i = 0; i < budget; i++) Batch.Add(Queue[i]);
                    Queue.RemoveRange(0, budget);
                }
                SkippedLastFrame = Queue.Count;
                Queue.Clear(); // пропущенные закажут кадр снова на следующем шаге

                if (_cmd == null) _cmd = new CommandBuffer { name = "ArcadiaEffects" };
                _cmd.Clear();
                int frame = Time.frameCount;
                for (int i = 0; i < Batch.Count; i++) Batch[i].Renderer.Record(_cmd, Batch[i].Time);
                Graphics.ExecuteCommandBuffer(_cmd);
                _cmd.Clear();
                for (int i = 0; i < Batch.Count; i++)
                {
                    Job j = Batch[i];
                    j.Renderer.EndFrame();
                    j.Renderer.LastRenderedFrame = frame;
                    j.Client?.OnArcadiaRendered(j.Renderer);
                }
                RenderedLastFrame = Batch.Count;
                Batch.Clear();
            }
            LastFlushMs = Watch.Elapsed.TotalMilliseconds;
        }

        internal static void RunnerDestroyed(ArcadiaEffectsRunner r)
        {
            if (_runner != r) return;
            _runner = null;
            Queue.Clear();
        }

        private static void EnsureRunner()
        {
            if (_runner != null) return;
            var go = new GameObject("ArcadiaEffectsSystem") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            _runner = go.AddComponent<ArcadiaEffectsRunner>();
        }
    }
}
