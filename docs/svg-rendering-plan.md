# SVG Rendering for Avalonia — Implementation Plan

Build an `Avalonia.Svg` library that parses SVG documents, compiles them into
`DrawingRecording` instances (static, compositor-bound for animated), and
replays them on every frame. SVG's `<symbol>` / `<use>` / `<defs>` concepts map
naturally onto shared, replayable recordings.

`DrawingRecording` is **not yet a stable API**, so its internal shape and
behavior may be changed freely where this plan requires it. `IDrawingContextImpl`
**is** stable and externally implementable; new backend capabilities are added
via probe interfaces (`IDrawingContextImplWith<Feature>`) rather than
modifications to `IDrawingContextImpl` itself.

## Branching & Review Strategy

**All `DrawingRecording` / `DrawingContext` API and recording-pipeline
evolution lands and is reviewed before any SVG code is written.** The SVG
branch consumes a finished recording primitive; it does not extend it.

Two branches, two reviews, in order:

1. **Phase 0 — `feature/drawing-recording`** off `upstream/master`. Brings
   `DrawingRecording` to the shape SVG needs: baseline + bounds/transform/
   ownership (R1–R5), bounds-change signal (R7), recording brush (G1),
   luminance masks (G2), and unified layer API for blend modes + group
   opacity + filter effects (G3). Phase 0 is broken into sub-phases that
   can land as separate commits or stacked PRs on the same branch; review
   happens once the full recording surface is in place.

   **Note:** R6 (element tags inside the recording) was prototyped and
   then withdrawn — element identity is an SVG-layer concern handled by
   the SVG compiler's own scene graph, not by the recording API.
2. **Phases 1–6 — `feature/svg-rendering`** off the merged Phase 0. Adds
   `Avalonia.Svg` (parser, compiler, control, hit testing, animation
   driver). **No changes to `DrawingRecording`, `DrawingContext`, or the
   recording pipeline.** If SVG uncovers a missing recording capability
   mid-phase, work stops and a follow-up PR against `DrawingRecording` is
   opened — it is not mixed into the SVG branch.

Rationale: reviewers evaluate the recording primitive once, as a coherent
surface, rather than chasing incremental extensions drip-fed through the SVG
phases. The SVG branch then reads as "here is a parser and compiler that
uses an existing API."

The only carve-outs allowed on the SVG branch are **backend-only**
implementations of already-declared probe interfaces (e.g. the Skia side of
`IDrawingContextImplWithLuminanceMask`) where the API shipped in Phase 0 but
the backend wasn't exercised. Even these should be rare; Phase 0 aims to
ship working Skia lowering for every API it adds.

## Architecture

Three layers:

- **Parser** — `SvgDocument` / `SvgElement` model parsed from XML
  (`XmlReader`-based, no LINQ-to-XML in hot paths), with a CSS-style style
  resolver.
- **Compiler** — `SvgCompiler` walks the model and emits `DrawingContext` calls
  into one or more `DrawingRecording` instances. `<symbol>` / heavily-referenced
  `<g>` become reusable sub-recordings.
- **Presentation** — `SvgImage : IImage` and a `Svg : Control` that call
  `context.DrawRecording(rootRecording)`.

Compositor binding:

- Use `DrawingRecording.Create(compositor, …)` when the SVG has SMIL
  animations or CSS-driven dynamic properties (mutable brushes/pens propagate
  via the compositor).
- Use the immutable `DrawingRecording.Create(…)` overload for static SVGs
  (the common case).

## SVG → DrawingContext Mapping Reference

| SVG | DrawingContext |
|---|---|
| `<rect>` | `DrawRectangle(brush, pen, rect, rx, ry)` |
| `<circle>`, `<ellipse>` | `DrawEllipse` |
| `<line>` | `DrawLine` |
| `<polyline>`, `<polygon>`, `<path>` | `StreamGeometry` → `DrawGeometry` |
| `<g transform>` | `PushTransform` |
| `<g opacity>` | `PushOpacity` |
| `<g clip-path>` | `PushGeometryClip` |
| `<g mask>` (alpha) | `PushOpacityMask` |
| `<g mask>` (luminance) | `PushOpacityMask` + `MaskType.Luminance` (new) |
| `<g filter>` | `PushLayer(new { Bounds = region, Effect = effect })` |
| `<image>` | `DrawImage` |
| `<text>`, `<tspan>` | `DrawGlyphRun` via `GlyphTypeface` + `FormattedText` |
| `linearGradient` | `ImmutableLinearGradientBrush` |
| `radialGradient` | `ImmutableRadialGradientBrush` |
| `pattern` | `DrawingRecordingBrush` (new `TileBrush`) |
| `<use>` | `DrawRecording(cachedRecording)` inside `PushTransform` |
| `<symbol>` | Pre-compiled reusable `DrawingRecording` |

## DrawingRecording Evolution (Phase 0 scope)

Phase 0 brings `DrawingRecording` to the shape SVG needs. Each item is
motivated by SVG but designed as a general-purpose primitive — nothing is
SVG-named or SVG-conditional. Reviewers evaluate the whole surface together.
`DrawingRecording` is not yet stable API, so API shape and observable
behavior may change freely.

| # | Change | Sub-phase | Why |
|---|---|---|---|
| R1 | `Bounds` returns real bounds synchronously for compositor-bound recordings (no "wait for compositor commit") | 0.2 | Any layout-sensitive consumer needs bounds before the first commit. |
| R2 | `DrawingRecording` explicitly retains child recordings referenced via `DrawRecording`; server-side refcounting | 0.2 | Shared sub-recordings (symbol libraries, component recordings) must survive independent parent disposal. |
| R3 | `DrawingRecording.GetBounds(Matrix)` — tight bounds under an outer transform | 0.2 | Non-axis-aligned transforms on a referenced recording need precise bounds, not a transformed AABB. |
| R4 | `DrawingContext.DrawRecording(DrawingRecording, Matrix)` overload | 0.2 | Common case of "draw this recording at this transform" fuses into one recorded node. |
| R5 | `DrawingRecordingOwnership { Owned, Shared }` parameter on `DrawRecording` | 0.2 | Makes the lifetime rule for nested recordings explicit rather than inferred from call order. |
| R6 | ~~Element tags inside the recording~~. **Dropped.** Element identity is an SVG-layer concern; the SVG compiler maintains its own per-element hit-test tree alongside the recording. The recording API stays free of identity tracking. | — | Avoid polluting the composition API with consumer-specific concepts. |
| R7 | Bounds-invalidation signal on compositor-bound recordings when mutable state changes the visible region | 0.3 | Animated brush `Transform` or pen `Thickness` can grow the drawn area; layout needs a callback. |
| G1 | `DrawingRecordingBrush : TileBrush` backed by a `DrawingRecording` (SVG gap 4.1) | 0.4 | Tiled content brush whose source is a recording. General-purpose; SVG `<pattern>` is one consumer. |
| G2 | `MaskType { Alpha, Luminance }` enum, `DrawingContext.PushOpacityMask(IBrush, Rect, MaskType)` overload, recording support, `IDrawingContextImplWithLuminanceMask` probe interface, Skia lowering (SVG gap 4.2) | 0.5 | Luminance masks are a general compositing primitive. |
| G3 | `DrawingContext.PushLayer(LayerOptions)` unifying blend modes, group opacity, and filter effects + `PushedStateType.Layer` + `RenderDataLayerNode` + bounds inflation for blur/drop-shadow + `IDrawingContextImplWithLayers` probe + Skia lowering (SVG gaps 4.3 mix-blend / `<g opacity>` / `<filter>`). Existing `IBlurEffect` and `IDropShadowEffect` reused; new `IColorMatrixEffect` / `IOffsetEffect` / `ICompositeEffect` deferred until SVG Phase 5 needs them. | 0.6 | Single layer abstraction covers SVG `mix-blend-mode`, group opacity, and `<filter>` — the three SVG features that all reduce to "save layer, apply paint on restore". |

**Explicitly not in Phase 0:**

- R6 (element tags inside the recording) — withdrawn. Element identity is
  an SVG-layer concern. The SVG compiler maintains its own per-element
  scene graph (bounds, transforms, clips, `pointer-events`) alongside the
  recording, since it already walks the element tree to emit draw calls.
  The recording API stays minimal and consumer-agnostic.
- R8 (`Rerecord` on compositor-bound recordings) — SVG's default strategy is
  many small swappable sub-recordings using R5 `Shared` ownership, which is
  sufficient. If benchmarks later demand it, R8 ships as a separate
  `DrawingRecording` follow-up PR — **not** on the SVG branch.
- Any SVG-specific helpers, marker types, or element-identity objects in
  the recording.

---

## Gap Summary (cross-referenced by phase)

"Recording-level" gaps land in Phase 0. SVG phases consume them.

| # | Gap | Phase | Branch | Recording change? |
|---|---|---|---|---|
| — | Baseline DrawingRecording concept | 0.1 | drawing-recording | Yes — baseline |
| 4.1 | Pattern brush backed by `DrawingRecording` | 0.4 (as G1) | drawing-recording | Yes |
| 4.2 | Luminance masks | 0.5 (as G2) | drawing-recording | Yes |
| 4.3 | Filter effects via `PushLayer` + `IEffect` | 0.6 (API as G3) + Phase 5 (new effect subtypes) | drawing-recording (API) + svg-rendering (compiler) | Partial — API in Phase 0.6, additional `IEffect` subtypes deferred to Phase 5 |
| 4.10 | Eager bounds for compositor-bound recordings | 0.2 (as R1) | drawing-recording | Yes |
| 4.11 | Explicit nested recording ownership | 0.2 (as R2, R5) | drawing-recording | Yes |
| 4.4 | Paint order | 3 | svg-rendering | No (compiler) |
| 4.5 | Stroke dash round-trip | 1 | svg-rendering | No (verification) |
| 4.6 | Text on path | 3 | svg-rendering | No (compiler) |
| 4.7 | Markers | 3 | svg-rendering | No (compiler) |
| 4.8 | Fill rule round-trip | 1 | svg-rendering | No (verification) |
| 4.9 | Visibility toggling | 6 | svg-rendering | No (compiler + R5 usage) |
| 4.12 | Gradient unit systems | 2 | svg-rendering | No (compiler) |
| 4.13 | Per-element hit testing | 6 | svg-rendering | No (SVG-layer scene graph; uses recording's `HitTest(Point)→bool` only as an early-out) |

---

## Phase 0 — DrawingRecording Feature-Complete

**Goal:** Bring `DrawingRecording` to the shape SVG needs, land and review it
as one coherent surface, then merge. After merge, the SVG branch consumes a
finished recording primitive and makes no further changes to it.

**Branch:** `feature/drawing-recording` off `upstream/master`. Sub-phases
below map to logical commit groups; the entire branch can ship as one PR or
as a short stack of PRs depending on review preference.

**Exit criteria:**

- All sub-phases 0.1 through 0.6 complete; 0.7 done; 0.8 done.
- Full solution builds on supported platforms.
- `DrawingRecordingTests`, `CompositorBoundDrawingRecordingTests`,
  `PatternBrushTests`, `LuminanceMaskTests`, effect tests all pass.
- PR(s) merged into `master`.
- `feature/svg-rendering` branches from the merge commit.

---

### Phase 0.1 — Baseline (complete)

Six commits already cherry-picked from the original author's work onto
`upstream/master`, stripped of glyph-outline entanglement:

```
Initial DrawingRecording implementation
Adjust DrawingRecording
Compositor aware DrawingRecording
Make render data ref countable
Throw for cross compositor usage
Fix DrawingRecording resource management
```

Net diff vs `upstream/master`: +1011 / −8 lines across 14 files. Public API
surface added:

- `Avalonia.Media.DrawingRecording` (static `Create(Action<DrawingContext>)`
  and `Create(Compositor, Action<DrawingContext>)`, `Bounds`, `HitTest`,
  `IDisposable`).
- `DrawingContext.DrawRecording(DrawingRecording)`.

Internal additions: `RenderItemList`, `RenderDataRecordingItemListNode`,
`RenderDataRecordingCompositionNode`, refcounted `CompositionRenderData`,
compositor factory methods, `PlatformDrawingContext` plumbing,
cross-compositor guard.

#### Checklist

- [x] Cherry-pick the six commits, dropping glyph-outline hunks, the
      resurrected `Avalonia.sln`, the stray `external/Avalonia.Controls.DataGrid`
      submodule entry, and `.claude/settings.local.json`.
- [x] `Avalonia.Base` builds clean on net8.0 and net10.0.
- [ ] Full solution build (after submodule init) clean.
- [ ] `DrawingRecordingTests` + `CompositorBoundDrawingRecordingTests` pass.

#### Tests (already in the branch)

- `tests/Avalonia.Base.UnitTests/Media/DrawingRecordingTests.cs` — immutable
  recordings: create, bounds, hit-test, dispose, basic draw-ops replay.
- `tests/Avalonia.Base.UnitTests/Composition/CompositorBoundDrawingRecordingTests.cs`
  — compositor-bound: animated brush propagation, change tracking,
  refcounting, cross-compositor guard.

---

### Phase 0.2 — Bounds & Ownership (R1, R2, R3, R4, R5)

The largest sub-phase. Puts the bounds/lifetime/transform story on firm
footing.

#### Checklist

- [ ] **R1** — `RenderDataDrawingContext` / `CompositionRenderData`
      accumulate bounds eagerly as items are appended. Both immutable and
      compositor-bound recordings expose correct `Bounds` immediately
      (synchronous for immutable; synchronous for compositor-bound after the
      record delegate returns, independent of the first commit).
- [ ] **R2** — Parent recordings retain child recordings referenced via
      `DrawRecording`. Server-side `CompositionRenderData` is refcounted;
      `Release` only disposes when the count reaches zero.
- [ ] **R3** — `DrawingRecording.GetBounds(Matrix)` walks the item list
      under the transform and unions per-item transformed bounds. Recurses
      into nested recordings.
- [ ] **R4** — `DrawingContext.DrawRecording(DrawingRecording, Matrix)`
      overload, recorded as a single node carrying the matrix; server-side
      fuses with the current transform at replay time.
- [ ] **R5** — `public enum DrawingRecordingOwnership { Owned, Shared }`;
      `DrawRecording(recording, ownership)` and
      `DrawRecording(recording, matrix, ownership)` overloads. Default on
      the existing `DrawRecording(recording)` becomes `Shared`. `Owned`
      disposes the child when the parent disposes; `Shared` leaves it to
      the external owner.
- [ ] Document ownership and lifetime rules on the `DrawingRecording` XML
      doc comments.

#### Tests

- `DrawingRecordingTests.EagerBounds_Immutable` — shapes appended produce
  expected bounds immediately after the record delegate returns.
- `CompositorBoundDrawingRecordingTests.EagerBounds` — fresh compositor-bound
  recording has valid bounds before the first commit; mutating a brush's
  geometry-affecting property updates bounds after commit.
- `DrawingRecordingTests.GetBounds_Transform` — known shapes under
  translate/scale/rotate/skew match a hand-computed tight bounds within
  floating-point tolerance.
- `DrawingRecordingTests.GetBounds_NestedRecording` — bounds propagate
  through a parent that references a child via `DrawRecording(child, m)`.
- `DrawingRecordingTests.DrawRecordingWithMatrix` — `DrawRecording(rec, m)`
  renders identically to `PushTransform(m) + DrawRecording(rec) + Pop` and
  snapshot shows one fused node.
- `DrawingRecordingTests.NestedOwnership_Shared` — disposing the parent of
  a `Shared` child does not dispose the child; the child remains
  hit-testable from another parent.
- `DrawingRecordingTests.NestedOwnership_Owned` — `Owned` child is disposed
  exactly once when the parent is disposed; double-dispose of parent is
  safe.
- `DrawingRecordingTests.NestedOwnership_Refcount` — a child referenced
  from three parents (all `Shared`, external owner disposes too) survives
  until the last reference releases.

---

### Phase 0.3 — Bounds-Change Signal (R7)

Adds a bounds-change signal for compositor-bound recordings.

> **R6 was withdrawn during implementation.** Element identity belongs in
> the SVG layer, not in the recording. See "DrawingRecording Evolution"
> for context.

#### Checklist

- [ ] **R7** — `DrawingRecording.BoundsChanged : event EventHandler<Rect>`
      raised on the UI thread when, after a compositor commit, the
      observable `Bounds` differs from the previously-observed value.
      Subscribing on an immutable recording throws
      `InvalidOperationException`; on a disposed recording throws
      `ObjectDisposedException`. Lazy hook: the recording subscribes to
      `Compositor.AfterCommit` only while there is at least one handler.

#### Tests

- `CompositorBoundDrawingRecordingTests.BoundsChanged_OnGrow` — animating a
  brush `Transform` that enlarges visible bounds fires the event once per
  commit.
- `CompositorBoundDrawingRecordingTests.BoundsChanged_NoOpWhenStable` —
  mutations that do not affect bounds do not raise the event.

---

### Phase 0.4 — `DrawingRecordingBrush` (G1 / SVG gap 4.1)

Tile brush whose source is a `DrawingRecording`.

#### Checklist

- [ ] `Avalonia.Media.DrawingRecordingBrush : TileBrush` with `Recording`,
      `SourceRect`, `DestinationRect`, `TileMode`, `Transform`.
- [ ] `ImmutableDrawingRecordingBrush` snapshot type.
- [ ] Serialization through `RenderDataDrawingContext` — the brush carries a
      refcounted reference to the server-side render data.
- [ ] Server-side brush implementation analogous to `ImageBrush`: realizes
      the recording into a tile and samples per the tile mode.
- [ ] Handles both immutable and compositor-bound recordings as brush
      source.

#### Tests

- `PatternBrushTests.TileModes` — none, tile, flip-x, flip-y, flip-xy.
- `PatternBrushTests.SourceAndDest` — cropping, scaling, translation via
  source-rect / dest-rect.
- `PatternBrushTests.CompositorBoundSource` — animated content inside the
  brush source reflects in rendered output.
- `PatternBrushTests.Disposal` — brush retains the recording for its
  lifetime; disposing the external recording while a brush references it
  (as `Shared`) is safe.
- `RenderTests.PatternBrush` — golden-image for known configurations.

---

### Phase 0.5 — Luminance Masks (G2 / SVG gap 4.2)

#### Checklist

- [ ] `Avalonia.Media.MaskType { Alpha, Luminance }` enum.
- [ ] `DrawingContext.PushOpacityMask(IBrush, Rect, MaskType)` overload;
      existing alpha-only overload forwards to this with `MaskType.Alpha`.
- [ ] `RenderDataDrawingContext` records the mask type on the opacity-mask
      node.
- [ ] `Avalonia.Platform.IDrawingContextImplWithLuminanceMask` probe
      interface (non-breaking addition to the backend surface).
- [ ] Skia implementation: luminance-to-alpha `SKColorFilter` applied to
      the mask layer; backend that doesn't implement the probe logs a
      one-shot warning and falls back to alpha.

#### Tests

- `DrawingContextMaskTests.AlphaDefault` — default behavior unchanged.
- `DrawingContextMaskTests.Luminance_SkiaBackend` — mask rendered from a
  gradient; compare to pre-computed alpha from the same gradient's luma.
- `RenderDataDrawingContextTests.RoundTrip_MaskType` — round-trip
  serialization preserves `MaskType`.
- `RenderTests.LuminanceMask` — golden image for a luminance mask against a
  colored background.

---

### Phase 0.6 — Layers (G3 / SVG gap 4.3)

Introduces a unified `PushLayer(LayerOptions)` API covering blend modes
(SVG `mix-blend-mode`), group opacity (`<g opacity>` semantics, distinct
from per-primitive `PushOpacity`), and filter effects (`<g filter>`) —
the three SVG features that all reduce to "save layer, apply paint on
restore" in Skia.

#### Checklist

- [x] `Avalonia.Media.LayerOptions` readonly record struct: `Bounds`
      (nullable), `Opacity` (nullable; null = 1.0), `BlendMode` reusing
      `BitmapBlendingMode` (Unspecified = SourceOver), `Effect`
      reusing `IEffect`.
- [x] `DrawingContext.PushLayer(LayerOptions)` + `PushedStateType.Layer`.
- [x] `IDrawingContextImplWithLayers` probe interface adds
      `PushLayer(LayerOptions)` overload of the existing
      `IDrawingContextImpl.PushLayer(Rect)` isolation primitive; pop is
      shared.
- [x] `RenderDataLayerNode` records the full `LayerOptions`. At replay,
      dispatches to the probe interface when available; otherwise
      composes `IDrawingContextImplWithEffects.PushEffect` + `PushOpacity`
      with a one-shot warning if the blend mode would be lost.
- [x] Bounds inflation for blur/drop-shadow effects on the layer.
- [x] `PlatformDrawingContext` mirrors the same probe + fallback dispatch
      for immediate rendering, balancing nested layers with a small
      `LayerFrame` stack.
- [x] `DrawingGroup` degrades a layer to per-primitive opacity (its graph
      doesn't preserve blend / effect).
- [x] Skia: implements `IDrawingContextImplWithLayers` by composing a
      single `SKPaint` (`Alpha` + `BlendMode` + `ImageFilter`) and
      calling `SaveLayer`.
- [ ] **Deferred:** new `IEffect` subtypes (`IColorMatrixEffect`,
      `IOffsetEffect`, `ICompositeEffect`). SVG Phase 5 (filters) will
      revisit when the compiler needs primitives beyond blur and
      drop-shadow.

#### Tests

- `DrawingRecordingTests.PushLayer_*` (7 unit tests) — passthrough,
  opacity-only, blend-mode-only, blur-effect bounds inflation,
  drop-shadow bounds expansion, explicit Bounds doesn't extend recording
  bounds, nested layer balance.
- `Avalonia.Skia.RenderTests.BlendModeRenderTests` (27 render tests) —
  every `BitmapBlendingMode` value exercised through `PushLayer`,
  organised into Porter-Duff, Separable, and HSL groups. Covers all SVG
  `mix-blend-mode` and COLR v1 `CompositeMode` modes.
- `Avalonia.Skia.RenderTests.DrawingRecordingTests.PushLayer_*` (3 render
  tests) — blend-mode multiply, group vs per-primitive opacity, blur
  effect.

---

## Phase 1 — Shapes & Paths

**Branch:** Begin `feature/svg-rendering` off the merge commit of Phase 0.
All subsequent phases extend this branch.

**Goal:** Static SVGs with basic shapes render correctly into a
`DrawingRecording`. No engine changes.

### Checklist

- [ ] Create `src/Avalonia.Svg/Avalonia.Svg.csproj` and wire into `Avalonia.slnx`.
- [ ] Parser skeleton: `SvgDocument.Load(Stream)` / `Load(Uri)` / `Parse(string)`.
- [ ] `XmlReader`-based element parser producing `SvgElement` tree.
- [ ] Attribute + inline-style resolver (no CSS cascade yet — presentation
      attributes and `style="..."` only).
- [ ] Unit parser (`px`, `pt`, `em`, `%`, unitless) → DIPs.
- [ ] Path-data (`d`) parser → `StreamGeometry`.
- [ ] Transform parser (`translate/rotate/scale/skewX/skewY/matrix`) → `Matrix`.
- [ ] `viewBox` + `preserveAspectRatio` → outer `Matrix`.
- [ ] Compiler emits `DrawRectangle`, `DrawEllipse`, `DrawLine`, `DrawGeometry`
      for `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`, `path`.
- [ ] Solid fill/stroke via `ImmutableSolidColorBrush` / `ImmutablePen`.
- [ ] `fill-rule` and `stroke-dasharray` / `stroke-dashoffset` round-trip
      verification (gaps 4.5, 4.8).
- [ ] `SvgImage : IImage` that owns the root `DrawingRecording` and draws it
      via `context.DrawRecording(...)`.

### Tests

All tests live under the SVG test assemblies — no additions to
`Avalonia.Base` tests.

- `Avalonia.Svg.UnitTests/ParserTests` — unit/transform/path-data parsers.
  - Covers malformed input, scientific notation, implicit commands, relative
    vs absolute path commands.
- `Avalonia.Svg.UnitTests/CompilerTests` — for each shape, snapshot the
  emitted `RenderItemList` structure (inspect via public `DrawingRecording`
  APIs or `Bounds`/`HitTest` observations). Includes dash and fill-rule
  round-trip coverage by rendering to a recording and inspecting behavior.
- `Avalonia.Svg.RenderTests` — bitmap reference diffs for a W3C SVG test
  subset (shapes, path, viewBox, stroke-dasharray, fill-rule).

---

## Phase 2 — Gradients, Clipping, `<use>` / `<symbol>` / `<defs>`

**Goal:** Recording reuse works; gradients and clipping render correctly.
Pure SVG compiler work — all recording primitives (R1–R5) already exist from
Phase 0.

### Checklist

- [ ] Linear/radial gradient parsing → immutable brushes.
- [ ] `gradientUnits="userSpaceOnUse"` vs `"objectBoundingBox"` +
      `gradientTransform` — compiler-side coordinate transform (gap 4.12).
- [ ] `clipPath` parsing and `PushGeometryClip` emission.
- [ ] `<defs>` resolution; `<symbol>` compiled once to a standalone
      `DrawingRecording`, cached on `SvgDocument`.
- [ ] `<use href>` emits a single `DrawRecording(symbolRec, transform,
      DrawingRecordingOwnership.Shared)` call at the use site. Attribute
      overrides (fill, stroke, opacity, …) apply to the use site via push
      states around the call.
- [ ] `SvgDocument.Dispose` releases its Phase-0-retained shared
      sub-recordings.
- [ ] `SvgImage.Bounds` uses `DrawingRecording.GetBounds(viewBoxToDest)`
      (from Phase 0 R3) so layout measurement is precise under non-trivial
      `preserveAspectRatio`/stretch.

### Tests

- `CompilerTests.Gradients` — linear/radial in both unit systems, with
  `gradientTransform`. Assert brush stops, spread method, transform.
- `CompilerTests.UseSymbol` — `<use>` emits one fused
  `DrawRecording(..., Shared)` node with the expected matrix; nested `<use>`
  works; override attributes apply.
- `CompilerTests.UseSymbolShared` — same `<symbol>` referenced 10× produces
  one sub-recording retained by 10 `Shared` references; document disposal
  releases all of them.
- `RenderTests` — W3C gradient + clipPath + use corpus.

---

## Phase 3 — Text, Markers, Paint Order

**Goal:** Text content (including text-on-path), SVG arrow markers, and
`paint-order` all render correctly. All changes compiler-side; no engine
changes.

### Checklist

- [ ] `<text>` / `<tspan>` layout: resolve font family/size/weight/style to
      `Typeface`; build `FormattedText` or direct `GlyphRun`.
- [ ] `text-anchor`, `dx`/`dy`, basic `x`/`y` offsets.
- [ ] `<textPath>` — arc-length sample the referenced path (gap 4.6 option
      (a)); emit one `PushTransform` + `DrawGlyphRun` per glyph cluster.
- [ ] `<marker>` — compile each marker once to a `DrawingRecording`; compute
      vertex positions + tangents from the owner path; emit
      `PushTransform(translate + rotate)` + `DrawRecording(markerRec)`
      (gap 4.7).
- [ ] `paint-order: stroke fill` — split into two `DrawGeometry` calls
      (gap 4.4).
- [ ] `fill-opacity`, `stroke-opacity`, and `opacity` composition rules.

### Tests

- `CompilerTests.Text` — glyph run positions for anchored/justified text,
  multi-`<tspan>` with inherited styles.
- `CompilerTests.TextOnPath` — straight path, curved path, closed path with
  wrap-around; glyph rotation matches tangent within tolerance.
- `CompilerTests.Markers` — start/mid/end markers on a polyline; rotation
  auto vs fixed; `markerUnits` = `strokeWidth` vs `userSpaceOnUse`.
- `CompilerTests.PaintOrder` — verify order of emitted items for default vs
  `paint-order: stroke fill`.
- `RenderTests` — W3C text + marker + textPath corpus.

---

## Phase 4 — Patterns & Masks

**Goal:** SVG `<pattern>` and `<mask>` compile correctly onto the
`DrawingRecordingBrush` and `MaskType.Luminance`/`Alpha` APIs already shipped
in Phase 0 (sub-phases 0.4 and 0.5). Pure SVG compiler work.

### Checklist

- [ ] SVG `<pattern>` → compile contents to a cached sub-`DrawingRecording`
      and wrap in a `DrawingRecordingBrush` with the pattern's `x`, `y`,
      `width`, `height`, `patternTransform`, `patternUnits`,
      `patternContentUnits`.
- [ ] Reference resolution: `<pattern href="#other">` inheritance chain.
- [ ] SVG `<mask>` → compile contents to a sub-recording, wrap in a brush,
      emit `PushOpacityMask(brush, bounds, MaskType.Luminance)` by default,
      `MaskType.Alpha` when `mask-type="alpha"` or `mask-mode="alpha"`.
- [ ] Unit-system conversions (`userSpaceOnUse` vs `objectBoundingBox`) for
      both pattern and mask.

### Tests

- `CompilerTests.Pattern` — tile mode, source/destination rect, transform;
  inheritance via `href`.
- `CompilerTests.Mask_Luminance` — default luminance mode; visual diff
  matches reference.
- `CompilerTests.Mask_Alpha` — `mask-type="alpha"` opts into alpha mode.
- `RenderTests` — W3C pattern + mask corpus.

---

## Phase 5 — Filter Effects

**Goal:** SVG `<filter>` compiles to
`PushLayer(new LayerOptions { Bounds = filterRegion, Effect = composedEffect })`
using the layer API already shipped in Phase 0.6. Pure SVG compiler work,
plus adding the new `IEffect` subtypes that Phase 0.6 deferred.

### Checklist

- [ ] SVG `<filter>` region (`x`, `y`, `width`, `height`,
      `filterUnits="userSpaceOnUse"` vs `"objectBoundingBox"`) →
      `LayerOptions.Bounds`.
- [ ] Add new `IEffect` subtypes (deferred from Phase 0.6) with their
      `Immutable…Effect` counterparts: `IColorMatrixEffect`,
      `IOffsetEffect`, `ICompositeEffect` (linear chain). Skia lowering
      via `SKColorFilter.CreateColorMatrix`,
      `SKImageFilter.CreateOffset`, `SKImageFilter.CreateCompose`.
      `EffectAnimator` registration so each new effect's parameters
      participate in transitions.
- [ ] Primitive compilers:
  - [ ] `feGaussianBlur` → `ImmutableBlurEffect`.
  - [ ] `feOffset` → `ImmutableOffsetEffect`.
  - [ ] `feColorMatrix` (matrix / saturate / hueRotate / luminanceToAlpha) →
        `ImmutableColorMatrixEffect`.
  - [ ] `feDropShadow` → `ImmutableDropShadowEffect`.
  - [ ] `feMerge` → `ImmutableCompositeEffect` with stages.
  - [ ] Stubs that log a one-shot warning for unsupported primitives.
- [ ] Chain linking via `in` / `in2` / `result`: collapse linear chains
      into `ImmutableCompositeEffect`; non-linear graphs are rejected with
      a warning (defer full DAG support).
- [ ] Compile `<g filter="url(#f)">` into
      `PushLayer(new LayerOptions { Bounds = region, Effect = composedEffect })`
      around the group's draw calls.

### Tests

- `CompilerTests.Filters_Primitives` — one test per supported primitive;
  assert the recorded `IImmutableEffect` tree matches expectation.
- `CompilerTests.Filters_Chain` — linear chain collapses into a composite
  of the expected length.
- `CompilerTests.Filters_Unsupported` — unsupported primitive emits a
  warning and the group renders without the filter.
- `RenderTests` — W3C filter corpus subset for supported primitives;
  golden diffs for blur, drop-shadow, color-matrix.

---

## Phase 6 — Interactivity & Animation

**Goal:** Interactive SVG: per-element hit testing, dynamic `visibility`, and a
minimal SMIL-to-compositor animation driver. Pure SVG work built on top of the
Phase 0 recording primitives (R5 ownership, R7 bounds signal). Element identity
and hit testing are tracked entirely in the SVG layer.

### Checklist

- [ ] At compile time, build a parallel `SvgHitNode` tree alongside the
      recording. Each `SvgHitNode` captures its `SvgElement`, accumulated
      transform, accumulated clip geometry, local bounds (computed from the
      same draw calls being emitted), and `PointerEvents`. For `<use>`
      references, the hit subtree of the referenced `<symbol>` is reused
      with the use site's transform/clip wrapped around it.
- [ ] Parse `pointer-events` (`none`, `all`, `visiblePainted`, …) and store
      on each `SvgElement` / `SvgHitNode`.
- [ ] `Svg` control: `HitTestElements(Point) : IEnumerable<SvgElement>`
      walks the `SvgHitNode` tree, inverting transforms and respecting
      clips at each level. Returns innermost-first (the SVG event-target
      order). Uses the recording's `HitTest(Point) : bool` as a cheap
      early-out.
- [ ] Route pointer events from the control to hit-tested elements; expose
      a `PointerPressedOnElement` event or similar.
- [ ] Subscribe to `DrawingRecording.BoundsChanged` (Phase 0 R7) on the
      `Svg` control to re-invalidate layout when animation changes the
      bounding box.
- [ ] Visibility toggling (gap 4.9):
  - [ ] Static: compile-time filter of `display: none` / `visibility: hidden`.
  - [ ] Dynamic: toggleable subtrees compile into their own
        `DrawingRecording` held as `Shared` children; the parent chooses
        whether to call `DrawRecording` on each per frame. Skipping a
        subtree does not dispose it.
- [ ] SMIL `<animate>` / `<set>` / `<animateTransform>` parser.
- [ ] Animation driver that drives mutable brushes, pens, and transforms on
      a compositor-bound root recording. Targets elements by id using the
      `Dictionary<string, SvgElement>` the compiler maintains.
- [ ] CSS animation + transition support (minimal: `animation-name` +
      keyframes + `transition: property duration`). Reuse existing
      `Avalonia.Animation` where possible.
- [ ] If benchmarks show structural animation is a bottleneck, file a
      separate `DrawingRecording` follow-up PR for R8 (`Rerecord`). **Do
      not** add it on the SVG branch.

### Tests

All SVG-side. The recording exposes only `HitTest(Point) → bool` and
`BoundsChanged` for these phases — the rest is `Avalonia.Svg`.

- `HitTestTests` — point-in-shape correctness across groups, transformed
  children, stroke vs fill hit regions, `pointer-events: none` elements
  excluded. Verify the `SvgHitNode` walker reproduces SVG's deepest-first
  event-target order.
- `VisibilityTests` — toggled subtree appears/disappears without
  recompiling the root recording; shared sub-recording survives repeated
  toggles (relies on Phase 0 R5 `Shared`).
- `BoundsChangedTests` — `Svg` control re-measures when an animation
  extends bounds; subscribes to the compositor-bound recording's event
  exactly once and unsubscribes on control detach.
- `SmilTests` — `<animate>` on `fill`, `stroke`, transform; timing
  (`begin`/`dur`/`repeatCount`); `calcMode` linear vs discrete; animation
  by element id resolves through the SVG-side id map.
- `IntegrationTests` — complete interactive SVG (hover state + click)
  round-trips through the `Svg` control.
- `RenderTests.Animation` — deterministic frames at sampled timestamps
  compared to reference bitmaps.

---

## Public API Surface (end state)

### Added by Phase 0 (`feature/drawing-recording`)

Recording primitive (0.1):

- `Avalonia.Media.DrawingRecording` (immutable + compositor-bound).
- `Avalonia.Media.DrawingContext.DrawRecording(DrawingRecording)`.

Bounds, transform, and ownership (0.2):

- `Avalonia.Media.DrawingRecordingOwnership` enum.
- `DrawingContext.DrawRecording(DrawingRecording, Matrix)` overload.
- `DrawingContext.DrawRecording(DrawingRecording, DrawingRecordingOwnership)`
  overload.
- `DrawingContext.DrawRecording(DrawingRecording, Matrix, DrawingRecordingOwnership)`
  overload.
- `DrawingRecording.GetBounds(Matrix)`.

Bounds-change signal (0.3):

- `DrawingRecording.BoundsChanged : event EventHandler<Rect>` on
  compositor-bound recordings (raised on the UI thread after a commit
  when `Bounds` differs from its previous value).

Recording brush (0.4):

- `Avalonia.Media.DrawingRecordingBrush : TileBrush, ISceneBrush`.
  (No immutable counterpart — matches `DrawingBrush`'s shape.)

Mask type (0.5):

- `Avalonia.Media.MaskType` enum.
- `DrawingContext.PushOpacityMask(IBrush, Rect, MaskType)` overload.
- `Avalonia.Platform.IDrawingContextImplWithLuminanceMask` probe interface.

Layers (0.6) — unifies group opacity, blend modes, and filter effects:

- `Avalonia.Media.LayerOptions` readonly record struct (`Bounds`,
  `Opacity`, `BlendMode` reusing `BitmapBlendingMode`, `Effect` reusing
  `IEffect`).
- `DrawingContext.PushLayer(LayerOptions)` + `PushedStateType.Layer`.
- `Avalonia.Platform.IDrawingContextImplWithLayers` probe interface
  (overload of the existing `IDrawingContextImpl.PushLayer(Rect)`
  isolation primitive; pop is shared).
- New `IEffect` subtypes (`IColorMatrixEffect`, `IOffsetEffect`,
  `ICompositeEffect`) — **deferred** to a follow-up PR. Existing
  `IBlurEffect` and `IDropShadowEffect` cover Phase 0's needs through
  `LayerOptions.Effect`. SVG Phase 5 will revisit if more primitives are
  required.

Observable behavior changes in `DrawingRecording` (all in Phase 0; allowed —
not yet stable API):

- `Bounds` returns real bounds synchronously in both immutable and
  compositor-bound modes (R1, Phase 0.2).
- Child recordings referenced via `DrawRecording` are retained by the
  parent until the parent is disposed; lifetime is refcounted on the
  server side. Default ownership on the existing `DrawRecording(rec)`
  becomes `Shared` (R2, R5, Phase 0.2).

### Added by Phases 1–6 (`feature/svg-rendering`)

```
Avalonia.Svg
├── SvgDocument           // Load/Parse; owns recordings; IDisposable.
├── SvgImage : IImage     // IImage implementation that draws the recording.
├── Svg : Control         // DPs: Source (Uri/Stream/string), Stretch,
│                         //      StretchDirection; raises hit events.
└── SvgSource             // Markup-extension friendly, cacheable.
```

**No additions to `Avalonia.Base`.** Phases 1–6 consume the Phase 0 API
exclusively. If a gap is discovered, it is addressed as a separate
`DrawingRecording` follow-up PR, not mixed into the SVG branch.

---

## Cross-Cutting Test Infrastructure

- **Golden-image corpus.** Curate a subset of the W3C SVG 1.1 test suite under
  `tests/Avalonia.Svg.RenderTests/Golden/`. Each `.svg` has a reference PNG
  rendered at a fixed DPI; tests diff with a PSNR/SSIM threshold.
- **Compiler snapshot tests.** Serialize emitted `RenderItemList` to a stable
  textual representation; compare to checked-in snapshots (like
  `DrawingRecordingTests`).
- **CI stability.** Render tests use a headless Skia backend (see existing
  `RenderTests` project) to keep output deterministic across platforms.
- **Benchmarks.** `BenchmarkDotNet` suite under `tests/Avalonia.Svg.Benchmarks`
  for parse → compile → first-frame on a representative set (logos, icons,
  maps, chart SVGs). Track regressions in CI.
