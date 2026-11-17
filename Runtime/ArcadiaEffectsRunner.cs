using UnityEngine;

namespace InteractiveSoftware.ArcadiaEffects
{
    /// <summary>Скрытый объект, который в конце кадра отдаёт <see cref="ArcadiaEffectsSystem"/> накопленные эффекты.</summary>
    [DefaultExecutionOrder(32000)]
    [AddComponentMenu("")]
    internal sealed class ArcadiaEffectsRunner : MonoBehaviour
    {
        private void LateUpdate() => ArcadiaEffectsSystem.Flush();

        private void OnDestroy() => ArcadiaEffectsSystem.RunnerDestroyed(this);
    }
}
