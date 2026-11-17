# Arcadia Effects

Проигрывание эффектов из редактора **Arcaidia Effector** (`D:\Progs\Arcadia-Effects\Arcada Effects`) прямо в Unity, без покадрового атласа.

Пространство имён: `InteractiveSoftware.ArcadiaEffects`.

## Как это устроено

Формат эффекта в программе — JSON (`library/*.json`: документ + текстуры в base64). Пакет читает этот файл как есть и повторяет движок редактора:

- **Симуляция** — порт `js/core/sim.js` один в один: шаг 1/120, тот же сидированный RNG (mulberry32), те же шумы и curl-поля, ветвление, зигзаг, суб-эмиттеры, путь движения, силовые поля. Сверено с оригиналом на всех 23 эффектах библиотеки: число частиц совпадает в каждой контрольной точке, расхождение координат ~1e-10.
- **Рендер** — порт `js/core/engine.js` в `RenderTexture`: режимы наложения Canvas2D (normal / add / screen / multiply) в премультиплицированной альфе, свечение частиц и слоёв, радиальное затухание, трейлы и ленты молний, листы-футажи, группы корректоров (`targets`), постэффекты **смаз**, **искажение** (displace) и **пиксель-арт**. Сравнение с кадрами программы: средняя разница меньше 1 из 255.

Вес: эффект — это JSON в несколько килобайт (плюс текстуры, если они есть), а не атлас кадров.

## Быстрый старт

1. Скопируйте файл эффекта из `library/` программы в проект Unity (станет `TextAsset`).
2. **GameObject → Effects → Arcadia Effect** (или добавьте компонент `ArcadiaEffect` на объект).
3. Назначьте файл в поле **Effect**. В инспекторе — ползунок времени и кнопка «Предпросмотр», эффект виден в сцене без Play Mode.

Для UI: **GameObject → UI → Arcadia Effect (UI)** (`ArcadiaEffectGraphic`, работает с Mask и RectMask2D, кнопка **Set Native Size**).

```csharp
using InteractiveSoftware.ArcadiaEffects;
using UnityEngine;

public class Example : MonoBehaviour
{
    [SerializeField] private ArcadiaEffect _hit;

    public void OnHit(Vector3 position)
    {
        _hit.transform.position = position;
        _hit.Play();
    }
}
```

## Компоненты

| Компонент | Где показывает |
| --- | --- |
| `ArcadiaEffect` | Квад в мире (`MeshRenderer`). Центр композиции = позиция объекта, размер = пиксели композиции / **Pixels Per Unit**. Sorting Layer / Order задаются на `MeshRenderer`. |
| `ArcadiaEffectGraphic` | uGUI. Композиция растягивается на `RectTransform`. |

### Настройки (Playback)

| Поле | Описание |
| --- | --- |
| `playOnEnable` | Запускать при включении объекта. |
| `loop` | Зацикливать по длительности композиции. |
| `speed` | Множитель скорости. |
| `unscaledTime` | Игнорировать `Time.timeScale`. |
| `onFinish` | По окончании: `KeepLastFrame`, `Hide`, `Disable` (выключить объект), `Destroy`. |
| `resolutionScale` | Масштаб `RenderTexture` относительно композиции (1 — пиксель в пиксель, как в программе). |
| `antiAliasing` | MSAA для лент трейлов и молний. |

Ещё у мирового компонента: **Blend** (`Normal` — как в программе, `Additive`), **Tint**, **Pixels Per Unit**.

### API

| Метод / свойство | Описание |
| --- | --- |
| `Play()`, `Play(fromTime)` | Запуск с начала / с момента. |
| `Pause()`, `Resume()`, `Stop()` | Пауза, продолжение, остановка (картинка очищается). |
| `SetTime(t)` | Показать кадр в момент `t` без запуска. |
| `Effect` | Файл эффекта (`TextAsset`); смена перезагружает эффект. |
| `LoadJson(json)` | Загрузить эффект из строки (например, из файла на диске). |
| `IsPlaying`, `Time`, `Duration`, `ParticleCount` | Состояние. |
| `Texture` | `RenderTexture` с готовой композицией — можно использовать в своём материале. |
| `OnFinished` | `UnityEvent` по окончании (без зацикливания). |

Низкоуровнево: `ArcadiaEffectData.Parse(json)` + `new ArcadiaEffectRenderer(data).Render(t)` рисуют любой момент в `RenderTexture` без компонентов.

## Ограничения

- **WebP-текстуры Unity не декодирует.** Если эффект содержит WebP (в библиотеке это `Capacitor` и `Poison`), в консоли будет ошибка с названием текстуры. Откройте в браузере `Tools~/ConvertWebpToPng.html` (папка пакета), перетащите туда JSON — скачается тот же файл с текстурами в PNG.
- Каждый экземпляр держит свою `RenderTexture` размером с композицию и каждый кадр рисует её заново. Для массовых мелких эффектов уменьшайте `resolutionScale`.
- Значения композиции считаются в гамме, как в браузере; в Linear-проекте шейдер вывода переводит их в линейное пространство, поэтому смешивание с фоном сцены идёт уже по правилам Unity.
- Работает в Built-in и URP (рендер в `RenderTexture` через `CommandBuffer`, шейдеры без зависимостей от пайплайна).
