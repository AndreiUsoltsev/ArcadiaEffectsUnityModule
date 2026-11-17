using System.Linq;
using UnityEditor;
using UnityEditor.UI;
using UnityEngine;

namespace InteractiveSoftware.ArcadiaEffects.Editor
{
    /// <summary>Общий предпросмотр в инспекторе: сведения об эффекте, ползунок времени, проигрывание вне Play Mode.</summary>
    internal static class ArcadiaPreviewGui
    {
        private static Object _playing;
        private static double _lastTime;

        public static bool IsPlaying(Object target) => _playing == target;

        public static void Draw(Object target, EffectDocument doc, float time, float duration, int particles,
            System.Action<float> setTime, System.Action<float> advance, System.Func<float> getPreview, System.Action<float> setPreview)
        {
            EditorGUILayout.Space();
            if (doc == null)
            {
                EditorGUILayout.HelpBox("Назначьте файл эффекта (library/*.json из Arcaidia Effector).", MessageType.Info);
                return;
            }
            int emitters = doc.Layers.Count(l => l.Type == LayerType.Emitter);
            EditorGUILayout.HelpBox(
                $"{doc.Name}\nКомпозиция {doc.CompW}x{doc.CompH}, {doc.Duration:0.###} с, наклон камеры {doc.CameraTilt:0.#}°\n" +
                $"Слоёв: {doc.Layers.Count} (эмиттеров {emitters}), текстур: {doc.Textures.Count}\nЧастиц сейчас: {particles}",
                MessageType.None);

            if (Application.isPlaying) return;

            using (new EditorGUILayout.HorizontalScope())
            {
                bool playing = _playing == target;
                if (GUILayout.Button(playing ? "❚❚ Пауза" : "▶ Предпросмотр", GUILayout.Width(120)))
                {
                    if (playing) StopPreview();
                    else StartPreview(target, advance);
                }
                EditorGUI.BeginChangeCheck();
                float t = EditorGUILayout.Slider(playing ? time : getPreview(), 0, Mathf.Max(0.01f, duration));
                if (EditorGUI.EndChangeCheck())
                {
                    StopPreview();
                    Undo.RecordObject(target, "Preview Time");
                    setPreview(t);
                    setTime(t);
                    EditorUtility.SetDirty(target);
                    SceneView.RepaintAll();
                }
            }
        }

        private static System.Action<float> _advance;

        private static void StartPreview(Object target, System.Action<float> advance)
        {
            StopPreview();
            _playing = target;
            _advance = advance;
            _lastTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
        }

        public static void StopPreview()
        {
            if (_playing == null) return;
            _playing = null;
            _advance = null;
            EditorApplication.update -= Tick;
        }

        private static void Tick()
        {
            if (_playing == null || Application.isPlaying) { StopPreview(); return; }
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)System.Math.Min(0.1, now - _lastTime);
            _lastTime = now;
            _advance?.Invoke(dt);
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
    }

    [CustomEditor(typeof(ArcadiaEffect))]
    [CanEditMultipleObjects]
    internal sealed class ArcadiaEffectEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script", "_previewTime");
            serializedObject.ApplyModifiedProperties();
            if (targets.Length != 1) return;
            var fx = (ArcadiaEffect)target;
            ArcadiaPreviewGui.Draw(fx, fx.Document, fx.Time, fx.Duration, fx.ParticleCount,
                fx.SetTime, fx.EditorAdvance, () => fx.PreviewTime, v => fx.PreviewTime = v);
            if (ArcadiaPreviewGui.IsPlaying(fx)) Repaint();
        }

        private void OnDisable() => ArcadiaPreviewGui.StopPreview();
    }

    [CustomEditor(typeof(ArcadiaEffectGraphic))]
    [CanEditMultipleObjects]
    internal sealed class ArcadiaEffectGraphicEditor : GraphicEditor
    {
        private SerializedProperty _effect, _blend, _playback, _onFinished;

        protected override void OnEnable()
        {
            base.OnEnable();
            _effect = serializedObject.FindProperty("_effect");
            _blend = serializedObject.FindProperty("_blend");
            _playback = serializedObject.FindProperty("_playback");
            _onFinished = serializedObject.FindProperty("_onFinished");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(_effect);
            EditorGUILayout.PropertyField(_blend);
            AppearanceControlsGUI();
            RaycastControlsGUI();
            MaskableControlsGUI();
            EditorGUILayout.PropertyField(_playback, true);
            EditorGUILayout.PropertyField(_onFinished);
            serializedObject.ApplyModifiedProperties();

            if (targets.Length != 1) return;
            var fx = (ArcadiaEffectGraphic)target;
            if (fx.Document != null && GUILayout.Button("Set Native Size"))
            {
                Undo.RecordObject(fx.rectTransform, "Set Native Size");
                fx.SetNativeSize();
            }
            ArcadiaPreviewGui.Draw(fx, fx.Document, fx.Time, fx.Duration, fx.ParticleCount,
                fx.SetTime, fx.EditorAdvance, () => fx.PreviewTime, v => fx.PreviewTime = v);
            if (ArcadiaPreviewGui.IsPlaying(fx)) Repaint();
        }

        protected override void OnDisable()
        {
            ArcadiaPreviewGui.StopPreview();
            base.OnDisable();
        }
    }

    /// <summary>Переимпорт JSON эффекта — сбросить кэш, чтобы сцена показала новую версию.</summary>
    internal sealed class ArcadiaEffectPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            foreach (string path in imported)
            {
                if (!path.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)) continue;
                var a = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
                if (a != null) ArcadiaEffectData.Invalidate("asset:" + a.GetInstanceID());
            }
        }
    }

    internal static class ArcadiaEffectMenu
    {
        [MenuItem("GameObject/Effects/Arcadia Effect", false, 10)]
        private static void CreateWorld(MenuCommand cmd)
        {
            var go = new GameObject("Arcadia Effect", typeof(MeshFilter), typeof(MeshRenderer), typeof(ArcadiaEffect));
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Arcadia Effect");
            AssignSelectedEffect(go.GetComponent<ArcadiaEffect>());
            Selection.activeObject = go;
        }

        [MenuItem("GameObject/UI/Arcadia Effect (UI)", false, 2100)]
        private static void CreateUI(MenuCommand cmd)
        {
            var parent = cmd.context as GameObject;
            if (parent == null || parent.GetComponentInParent<Canvas>() == null)
            {
                var canvas = Object.FindFirstObjectByType<Canvas>();
                parent = canvas != null ? canvas.gameObject : null;
            }
            var go = new GameObject("Arcadia Effect (UI)", typeof(RectTransform), typeof(CanvasRenderer), typeof(ArcadiaEffectGraphic));
            GameObjectUtility.SetParentAndAlign(go, parent);
            ((RectTransform)go.transform).sizeDelta = new Vector2(256, 256);
            go.GetComponent<ArcadiaEffectGraphic>().raycastTarget = false;
            Undo.RegisterCreatedObjectUndo(go, "Create Arcadia Effect (UI)");
            Selection.activeObject = go;
        }

        private static void AssignSelectedEffect(ArcadiaEffect fx)
        {
            var json = Selection.objects.OfType<TextAsset>().FirstOrDefault();
            if (json == null) return;
            fx.Effect = json;
            EditorUtility.SetDirty(fx);
        }
    }
}
