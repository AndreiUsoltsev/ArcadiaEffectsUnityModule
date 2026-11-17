# Arcadia Effects (Unity-модуль) — инструкции для агентов

UPM-пакет `com.interactivesoftware.arcadiaeffects`: проигрывание эффектов редактора
**Arcaidia Effector** (https://github.com/AndreiUsoltsev/Arcadia-Effects) в Unity без
покадрового атласа. Пользовательская документация — `README.md`.

## Главный инвариант: модуль = порт движка редактора

Это не «похожий» рендер, а перенос `Arcada Effects/js/core/*` один в один. Эффект в игре
обязан выглядеть так же, как в редакторе. Отсюда правила:

1. **Не «улучшать» алгоритмы ядра.** Порядок вызовов rng, `|0`/`Math.imul`/`Math.round`-семантика,
   `||`-дефолты (`x || 60` ≠ `x ?? 60`), сравнения с `1e-9` — всё как в JS. Источник правды —
   код редактора, а не здравый смысл. Перед правкой `Runtime/Core/*` читать соответствующий
   `js/core/*.js` и `skills/afx-core-engine/SKILL.md` редактора.
2. **Новое в редакторе → то же здесь.** Поле формата (с тем же дефолтом, что в `Model.migrate`/
   фабриках), механизм симуляции, режим рендера, постэффект, процедурная фигура. Таблица
   соответствия — `skills/afx-unity-module/SKILL.md` в репозитории редактора.
3. **Детерминизм.** В `Runtime/Core` нет `UnityEngine`, `Random`, времени: ядро собирается
   обычным .NET (так работает проверка паритета).
4. **Оптимизации — только с доказательством паритета** (см. «Проверка»). Отличия от редактора
   допустимы только на уровнях качества Medium/Low и только в рендере (не в симуляции,
   кроме капа частиц `maxParticles`).

## Карта

```
Runtime/Core/          порт js/core без UnityEngine
  Js.cs                  utils.js: JS-арифметика, mulberry32, noise1/2/3, curl2, turb3, turbCurl3, bezierEase
  Json.cs                свой парсер/писатель JSON (поле бывает числом, цветом или треком)
  Track.cs               track.js + curve.js (Curve, ColorRamp)
  Model.cs               model.js: документ, слои, дефолты hydrate/migrate, forceIndex, pixelGridOf, Features
  PathBake.cs            path.js
  Simulation.cs          sim.js (пул частиц, без аллокаций в шаге)
Runtime/Rendering/     engine.js + sprites.js
  ArcadiaEffectRenderer.cs          кадр, слои, частицы, трейлы, прямые слои (без холста)
  ArcadiaEffectRenderer.Batching.cs цели, пакеты, штрихи трейлов (объединение через глубину)
  ArcadiaEffectRenderer.Post.cs     postfx: mblur, displace (сетка на GPU), pixelate; блюр свечения
  GeometryBuffer.cs      один меш на кадр, сабмеши = пакеты (порядок вызовов Mesh API важен!)
  ShapeRasterizer.cs     процедурные фигуры sprites.js (CPU), свечение
  EffectResources.cs     ArcadiaEffectData (кэш документов/текстур), ShapeAtlas
  RenderPrimitives.cs    Affine, материалы по режимам наложения
Runtime/Resources/ArcadiaEffects/*.shader   Draw (частицы), Post (проходы), Display (мир), DisplayUI
Runtime/ArcadiaEffect*.cs   компоненты, плеер, система (один CommandBuffer на кадр), настройки качества, ассет .afx
Editor/                инспекторы, ScriptedImporter .afx, меню
Tests/                 EditMode: модель/rng, паритет на эталонных значениях, регрессия кадров (Golden)
Tools~/parity/         паритет симуляции с JS по всей library редактора (Unity не нужен)
Tools~/ConvertWebpToPng.html  перекодировать WebP-текстуры эффекта в PNG
```

## Проверка (обязательно после правок ядра или рендера)

1. **Паритет симуляции** — `Tools~/parity/README.md`: `countMismatch=0`, `maxRelErr` ~1e-10.
2. **Тесты Unity** (EditMode): `RenderRegressionTests` сравнивает кадры с кадрами редактора
   (`Tests/Data/Golden/<эффект>@<время>.png`, уменьшены в 4 раза, поверх чёрного;
   пороги mean < 1/255, p99 ≤ 10). Запуск без открытого редактора проекта:
   `Unity -batchmode -projectPath <проект> -runTests -testPlatform EditMode -testResults r.xml`.
3. **Новый эталон кадра** — снять в редакторе (сервер `run.bat`, консоль вкладки):
   `new AFX.Engine().render(doc, t, ctx)` на холст размера композиции поверх чёрного, PNG
   уменьшить в 4 раза площадным усреднением, положить как `Golden/<эффект>@<t>.png`, JSON
   эффекта — в `Tests/Data/`.
4. Компиляция без Unity: проект csproj со ссылками на `Editor/Data/Managed/UnityEngine/*.dll`
   и `Library/ScriptAssemblies/UnityEngine.UI.dll` ловит ошибки C# за секунды.

## Подводные камни (уже наступали)

- `Mesh.SetIndexBufferParams` сбрасывает сабмеши до одного, а сеттер `subMeshCount`
  перекраивает индексный буфер. Число сабмешей задавать только через `SetSubMeshes`,
  посреди записи кадра — с ПУСТЫМИ описаниями (данные кадра ещё не залиты).
- `CommandBuffer.DrawMesh` проверяет номер сабмеша при записи — число сабмешей растёт сразу
  в `GeometryBuffer.Close`.
- `GetInstanceID()` в Unity 6.5 — ошибка компиляции. Ключи кэша — `ArcadiaEffectData.KeyOf`.
- Цвета вершин — half, упаковка один раз на примитив (`HalfColor.Pack`), не на вершину.
- Прямые слои смешиваются со сценой в Linear-пространстве — поэтому на High они выключены.
- WebP Unity не декодирует — импортёр пропускает такие текстуры с предупреждением.

## Версии и релиз

`package.json` → `version` по semver (ломающее API — мажор). Каждый коммит с правкой
ядра — с прогоном паритета и тестов; в сообщении коммита — что сверено.
