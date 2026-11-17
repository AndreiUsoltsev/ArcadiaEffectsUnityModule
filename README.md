# Arcadia Effects

Проигрывание эффектов из редактора **Arcaidia Effector** (`D:\Progs\Arcadia-Effects\Arcada Effects`) прямо в Unity, без покадрового атласа. Готово для ПК, WebGL 2 и мобильных (GLES3 / Vulkan / Metal).

Пространство имён: `InteractiveSoftware.ArcadiaEffects`.

Редактор эффектов: https://github.com/AndreiUsoltsev/Arcadia-Effects. Инструкции для агентов (правила порта движка, проверка паритета) — `CLAUDE.md`.

## Как это устроено

Формат эффекта в программе — JSON (`library/*.json`: документ и текстуры в base64). Пакет повторяет движок редактора:

- **Симуляция** — порт `js/core/sim.js` один в один: шаг 1/120, тот же сидированный RNG, шумы, ветвление, суб-эмиттеры, пути, силовые поля. Сверено с оригиналом на всех 23 эффектах библиотеки: число частиц совпадает во всех 1800 контрольных точках, координаты расходятся на ~1e-10.
- **Рендер** — порт `js/core/engine.js`: наложение Canvas2D в премультиплицированной альфе, свечение частиц и слоёв, затухание, трейлы, листы-футажи, группы корректоров, постэффекты смаз / искажение / пиксель-арт.
- На уровне **High** кадр совпадает с программой: средняя разница < 1/255 (регрессионный тест против кадров самой программы).

## Быстрый старт

1. **Assets → Arcadia Effects → Import from Arcaidia library…** — выберите `library/*.json`, он скопируется в проект как `.afx` (ассет `ArcadiaEffectAsset`). Уже лежащие в проекте `.json` переводятся командой **Assets → Arcadia Effects → Convert JSON to .afx**.
2. **GameObject → Effects → Arcadia Effect**, назначьте ассет в поле **Asset**. В инспекторе — ползунок времени и предпросмотр без Play Mode.
3. Для UI — **GameObject → UI → Arcadia Effect (UI)** (`ArcadiaEffectGraphic`, работает с Mask и RectMask2D, кнопка **Set Native Size**).

```csharp
using InteractiveSoftware.ArcadiaEffects;
using UnityEngine;

public class Example : MonoBehaviour
{
    [SerializeField] private ArcadiaEffectAsset _hit;

    private void Awake() => ArcadiaEffectsSystem.Prewarm(_hit); // без рывка при первом показе

    public void OnHit(Vector3 position)
    {
        var go = new GameObject("Hit", typeof(MeshFilter), typeof(MeshRenderer), typeof(ArcadiaEffect));
        go.transform.position = position;
        var fx = go.GetComponent<ArcadiaEffect>();
        fx.Playback.onFinish = ArcadiaFinishAction.Destroy;
        fx.Asset = _hit; // загрузка + Play
    }
}
```

## Импорт `.afx` (рекомендуется для продакшена)

`ArcadiaEffectImporter` превращает файл эффекта в ассет:

- текстуры декодируются и премультиплицируются при импорте; **Texture Compression**: `Automatic` — ПК без сжатия (один в один с программой), Android/iOS — ASTC 6x6, WebGL — DXT5 (если стороны кратны 4);
- атласы процедурных фигур (частицы, их свечение) запекаются при импорте — в игре нет растеризации и гауссова размытия;
- в игре остаётся разобрать небольшой JSON документа (единицы–десятки КБ) — без base64 и декодирования PNG.

`TextAsset` с JSON и `LoadJson(string)` продолжают работать как запасной путь: тогда всё это делается при загрузке.

## Уровни качества

Уровень задаётся в настройках проигрывания компонента (`Playback.quality`), глобально в ассете настроек или в коде (`ArcadiaEffectsSettings.QualityOverride`). `Auto` выбирает: ПК — **High**, WebGL и мобильные — **Medium**, слабые мобильные (мало памяти или ядер) — **Low**.

| Параметр | High (= программа) | Medium | Low |
| --- | --- | --- | --- |
| Макс. разрешение холста | 1 | 0,75 | 0,5 |
| Разрешение по размеру на экране | да (не ниже ½) | да | да |
| Прямые слои без RenderTexture | нет | да | да |
| Трейлы | MSAA×4, точная геометрия | MSAA×2 | полосы, без MSAA |
| Блюр свечения слоя (sigma, px) | ≤ 6 | ≤ 3 | ≤ 2 |
| Смаз, сэмплов | как в файле | ≤ 8 | ≤ 4 |
| Искажение | как в файле | сетка ×2 | выкл. |
| Перерисовок в секунду | каждый кадр | ≤ 60 | ≤ 30 |
| Частиц на эмиттер | 5000 | 2500 | 1200 |

Все значения настраиваются: **Create → Arcadia Effects → Settings**, ассет положить в `Resources/ArcadiaEffectsSettings.asset`. Там же **Max Renders Per Frame** — сколько эффектов перерисовывать за кадр (остальные показывают прошлый кадр, симуляция догоняет время).

**Прямые слои.** Слои без свечения слоя, затухания, screen/multiply и постэффектов рисуются сразу в сцену (меш с сабмешами), без RenderTexture. Эффекты, где таких слоёв все (Sparks, Shockwave, Farm-эффекты и т. п.), вообще не держат RenderTexture. В Linear-проекте такие слои смешиваются со сценой в линейном пространстве, а не в гамме, как в программе: свечения выглядят немного мягче. Поэтому на High прямые слои выключены.

## Компоненты

| Компонент | Где показывает |
| --- | --- |
| `ArcadiaEffect` | В мире (`MeshRenderer`). Центр композиции = позиция объекта, размер = пиксели композиции / **Pixels Per Unit**. Sorting Layer / Order — на `MeshRenderer`. |
| `ArcadiaEffectGraphic` | uGUI, композиция растягивается на `RectTransform`. В UI эффект всегда идёт через холст (у Graphic одна текстура). |

### Настройки (Playback)

| Поле | Описание |
| --- | --- |
| `playOnEnable` | Запускать при включении объекта. |
| `loop` | Зацикливать по длительности композиции. |
| `speed` | Множитель скорости. |
| `unscaledTime` | Игнорировать `Time.timeScale`. |
| `onFinish` | По окончании: `KeepLastFrame`, `Hide`, `Disable`, `Destroy`. |
| `quality` | Уровень качества (`Auto` по умолчанию). |
| `resolutionScale` | Множитель разрешения холста поверх уровня качества. |

### API

| Метод / свойство | Описание |
| --- | --- |
| `Play()`, `Play(fromTime)` | Запуск с начала / с момента. |
| `Pause()`, `Resume()`, `Stop()` | Пауза, продолжение, остановка (холст освобождается). |
| `SetTime(t)` | Показать кадр в момент `t` без запуска. |
| `Asset` / `Effect` | Эффект как `ArcadiaEffectAsset` (.afx) / как `TextAsset` (.json). |
| `LoadJson(json)` | Загрузить эффект из строки. |
| `IsPlaying`, `Time`, `Duration`, `ParticleCount` | Состояние. |
| `Texture` | Холст композиции (null, если эффект целиком рисуется прямыми слоями или скрыт). |
| `OnFinished` | `UnityEvent` по окончании (без зацикливания). |
| `ArcadiaEffectsSystem.Prewarm(asset)` | Подготовить эффект заранее (материалы, шейдеры, пул RT). |
| `ArcadiaEffectsSystem.LastFlushMs`, `RenderedLastFrame`, `SkippedLastFrame` | Счётчики для профилирования. |

Низкоуровнево: `new ArcadiaEffectRenderer(data, quality).Render(t)` рисует любой момент в `Output` без компонентов.

## Производительность

Что сделано для мобильных и веба:

- **Один CommandBuffer на кадр** для всех эффектов (`ArcadiaEffectsSystem`), один меш на эффект на кадр.
- **Нет GC в кадре**: пул частиц, struct-RNG, переиспользуемые буферы.
- **RenderTexture только при необходимости**: MSAA и буфер глубины — только у эффектов с трейлами; холст берётся из пула и освобождается, когда эффект скрыт или закончился; невидимые эффекты не перерисовываются; разрешение холста падает, когда эффект мал на экране.
- **Трейлы**: тысячи штрихов рисуются одним вызовом (повторные пиксели штриха отсекаются буфером глубины), точки пути прорежены до ~пикселя.
- **Искажение пламени** считается на GPU (тот же целочисленный шум, что в программе); на устройствах без half-RenderTexture — на CPU.

Замеры (редактор, Mono, ПК; IL2CPP-сборки быстрее), CPU на эффект, High: Explosion 0,2 мс, Booba 0,7 мс, Flame Pro 3,2 мс, Magic Ice Firework 0,3 мс, saber (4300 частиц-трейлов) 15 мс; в самой программе (браузер) эти кадры стоят 21–94 мс.

Для замеров на устройстве — компонент `ArcadiaBenchmark` из `Assets/ArcadiaEffectsDemo` (сетка эффектов, счётчики на экране, Q — уровень качества, +/− — число эффектов) и маркеры профайлера `ArcadiaEffects.*`.

## Ограничения

- **WebP-текстуры Unity не декодирует.** Импортёр пропустит их с предупреждением. Откройте `Tools~/ConvertWebpToPng.html` (папка пакета), перетащите туда JSON — скачается тот же файл с текстурами в PNG.
- Трейлы с наложением multiply рисуются приближённо: второй проход умножения может лечь на перекрытие внутри штриха дважды (редкий случай).
- Работает в Built-in и URP; рендер в RenderTexture через `CommandBuffer`, шейдеры без зависимостей от пайплайна.
