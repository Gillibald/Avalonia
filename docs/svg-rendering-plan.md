# SVG Rendering for Avalonia — Implementation Plan

Build an `Avalonia.Svg` library that parses SVG documents, compiles them into
`DrawingRecording` instances (static, compositor-bound for animated), and
replays them on every frame. SVG's `<symbol>` / `<use>` / `<defs>` concepts map
naturally onto shared, replayable recordings.

`DrawingRecording` is **not yet a stable API**, but Phase 0 brought it to a
feature-complete, hardened shape that is not expected to diverge much —
**SVG support is the proof of concept that validates it**.
`IDrawingContextImpl` **is** stable and externally implementable; new
backend capabilities are added via probe interfaces
(`IDrawingContextImplWith<Feature>`) rather than modifications to
`IDrawingContextImpl` itself.

## Branching & Review Strategy

**SVG development does not wait for `DrawingRecording` to merge.** Phase 0
is feature-complete and audited against every SVG consumer in this plan;
its shape is treated as settled. The SVG implementation starts immediately
as the proof of concept — a real consumer whose feedback strengthens the
Phase 0 review instead of trailing it.

Two branches, separate concerns, developed in parallel:

1. **Phase 0 — `feature/drawing-recording`** off `upstream/master`.
   Feature-complete: baseline + bounds/transform/ownership (R1–R5),
   bounds-change signal (R7), recording brush (G1), luminance masks (G2),
   unified layer API for blend modes + group opacity + filter effects
   (G3), and the immutable resource policy. Under review; merges to
   `master` on its own schedule.

   **Note:** R6 (element tags inside the recording) was prototyped and
   then withdrawn — element identity is an SVG-layer concern handled by
   the SVG compiler's own scene graph, not by the recording API.
2. **Phases 1–6 — `feature/svg-rendering`**, stacked on
   `feature/drawing-recording` and started immediately. Adds `Avalonia.Svg`
   (parser, compiler, control, hit testing, animation driver). **No changes
   to `DrawingRecording`, `DrawingContext`, or the recording pipeline land
   on this branch.** If SVG uncovers a missing or wrong recording
   capability, the fix is a commit (or follow-up PR) on the *recording*
   side and the SVG branch rebases onto it — SVG work continues rather
   than stopping. Once Phase 0 merges, the SVG branch rebases onto
   `master`.

Rationale: the recording PR stays one coherent, reviewable surface, and the
SVG branch reads as "here is a parser and compiler that uses an existing
API" — while the proof of concept exercises that API early enough for any
findings to flow into the Phase 0 review rather than after it.

The only carve-outs allowed on the SVG branch are **backend-only**
implementations of already-declared probe interfaces (e.g. the Skia side of
`IDrawingContextImplWithLuminanceMask`) where the API shipped in Phase 0 but
the backend wasn't exercised. Even these should be rare; Phase 0 ships
working Skia lowering for every API it adds.

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

## Spec Target

Neither "SVG 1.1" nor "SVG 2.0" alone describes what real content needs:
SVG 2 never left Candidate Recommendation and re-homed half its features
into CSS modules, while strict 1.1 misses syntax that modern exporters
emit. The target is the profile resvg and librsvg converged on:

- **Feature scope:** SVG 1.1 Full, minus the legacy SVG 2 removed
  (`<tref>`, `<altGlyph>`, SVG fonts, `enable-background`), plus the
  SVG 2 / CSS-module features that are table stakes in current content:
  - plain `href` accepted everywhere alongside legacy `xlink:href`
    (Phase 1 parser rule);
  - `rx` / `ry` `auto` keyword on `<rect>` / `<ellipse>` (Phase 1);
  - `paint-order` (Phase 3, already planned);
  - `mix-blend-mode` / `isolation` via the Phase 0.6 layer API (Phase 3);
  - `orient="auto-start-reverse"` on markers (Phase 3);
  - `mask-type` / `mask-mode` (Phase 4, already planned).
- **Interpretation:** wherever SVG 1.1 and SVG 2 (or its CSS successors —
  CSS Transforms, CSS Masking, CSS Compositing & Blending, Filter Effects
  Level 1) both define a behavior, the SVG 2 / CSS text is normative; it
  is the cleaned-up wording browsers actually implement.
- **Excluded SVG 2 features** (unshipped anywhere, absent from content,
  and — for mesh gradients/hatches — the ones that would require new
  recording primitives): listed under Non-Goals.

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

## Non-Goals (initial scope)

Explicitly out of scope for Phases 1–6; revisit on demand afterwards:

- `<foreignObject>`, scripting (`<script>`, DOM APIs), and declarative
  interactivity beyond the pointer events in Phase 6.
- Full CSS cascade (external stylesheets, selector machinery beyond the
  minimal resolver). Phase 1 supports presentation attributes + inline
  `style`; Phase 6 adds only minimal CSS animations/transitions.
- `vector-effect="non-scaling-stroke"`.
- `color-interpolation-filters="linearRGB"`: filters and luminance masks
  operate in sRGB (matching common browser behavior), diverging from the
  SVG 1.1 default on the letter of the spec.
- Full SMIL timing — only `begin`/`dur`/`repeatCount` and
  linear/discrete `calcMode` (Phase 6).
- Non-linear filter primitive graphs — linear `in`/`result` chains only
  (Phase 5).
- SVG 2 features with no browser implementation and no real-world content:
  mesh gradients, `<hatch>`, the SVG 2 text-layout model (`inline-size`,
  `shape-inside`, automatic wrapping), and the `miter-clip` / `arcs` line
  joins. Mesh gradients and hatches would also require new recording
  primitives, putting them doubly out of scope.
- SVG 1.1 legacy that SVG 2 removed: `<tref>`, `<altGlyph>`, SVG fonts,
  `enable-background` / the `BackgroundImage` filter inputs.
- Geometry properties via CSS (`d: path(…)`, `cx:` etc.) — deferred along
  with the minimal-CSS scope.
- A dedicated rasterized-export API: `SvgImage` → bitmap is
  `RenderTargetBitmap.CreateDrawingContext()` + `context.DrawRecording(...)`,
  which already works.

## DrawingRecording Evolution (Phase 0 scope)

Phase 0 brings `DrawingRecording` to the shape SVG needs. Each item is
motivated by SVG but designed as a general-purpose primitive — nothing is
SVG-named or SVG-conditional. Reviewers evaluate the whole surface together.
`DrawingRecording` is not yet stable API, so API shape and observable
behavior may change freely.

| # | Change | Sub-phase | Why |
|---|---|---|---|
| R1 | `Bounds` returns real bounds synchronously for compositor-bound recordings (no "wait for compositor commit") | 0.2 | Any layout-sensitive consumer needs bounds before the first commit. |
| R2 | `DrawingRecording` explicitly retains child recordings referenced via `DrawRecording`; client-side (UI-thread) refcounting that releases the server data on the next batch | 0.2 | Shared sub-recordings (symbol libraries, component recordings) must survive independent parent disposal. |
| R3 | `DrawingRecording.GetBounds(Matrix)` — per-item bounds under an outer transform (each top-level item's AABB is transformed individually before union; nested structures contribute their local AABB) | 0.2 | Non-axis-aligned transforms on a referenced recording need tighter bounds than a single transformed AABB. Exact for the axis-aligned viewBox/stretch case SVG layout uses. |
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
| 4.3 | Filter effects via `PushLayer` + `IEffect` | 0.6 (API as G3) + recording-side follow-up (new effect subtypes, consumed by Phase 5) | drawing-recording (API + effect subtypes) + svg-rendering (compiler) | Partial — API in Phase 0.6; additional `IEffect` subtypes land recording-side when Phase 5 needs them |
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

- All sub-phases 0.1 through 0.6 complete.
- `DrawingRecordingBenchmarks` (BenchmarkDotNet) and the RenderDemo
  `DrawingRecordingPage` exist for perf tracking and manual validation.
- Full solution builds on supported platforms.
- `DrawingRecordingTests`, `CompositorBoundDrawingRecordingTests`,
  `DrawingRecordingBrushTests` and the Skia render tests
  (`DrawingRecordingTests`, `BlendModeRenderTests`) all pass.
- PR(s) merged into `master` — proceeds in parallel with SVG development
  and is **not** a gate for starting Phases 1–6.
- `feature/svg-rendering` is stacked on this branch and rebases onto
  `master` once Phase 0 merges.

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
- [x] `DrawingRecordingTests` + `CompositorBoundDrawingRecordingTests` pass.

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

- [x] **R1** — `RenderDataDrawingContext` / `CompositionRenderData`
      accumulate bounds eagerly as items are appended. Both immutable and
      compositor-bound recordings expose correct `Bounds` immediately
      (synchronous for immutable; synchronous for compositor-bound after the
      record delegate returns, independent of the first commit). Leaf nodes
      prefer the client-side pen for bounds so pending pen mutations are
      visible to UI-thread queries before the next commit.
- [x] **R2** — Parent recordings retain child recordings referenced via
      `DrawRecording`. `CompositionRenderData` is refcounted on the client
      (UI thread) via `CompositionRenderDataResourceRef` in the parent's
      resource list; reaching zero releases the server data on the next
      batch. (Refcounting on the UI thread avoids cross-thread disposal.)
- [x] **R3** — `DrawingRecording.GetBounds(Matrix)` walks the top-level
      item list under the transform and unions per-item transformed AABBs.
      Nested recordings and push scopes contribute their local AABB as one
      item (no recursion) — exact for axis-aligned transforms, conservative
      for rotation/skew over nested multi-item content.
- [x] **R4** — `DrawingContext.DrawRecording(DrawingRecording, Matrix)`
      overload, recorded as a single node carrying the matrix; replay
      fuses it with the current transform, bounds/hit-test apply it
      per-item (inverse-transformed point for hit tests).
- [x] **R5** — `public enum DrawingRecordingOwnership { Owned, Shared }`;
      `DrawRecording(recording, ownership)` and
      `DrawRecording(recording, matrix, ownership)` overloads. Default on
      the existing `DrawRecording(recording)` becomes `Shared`. `Owned`
      disposes the child when the parent disposes (including when the
      record delegate throws); `Shared` leaves it to the external owner.
      Ownership is honored only by recording-building contexts; the visual
      content recorder and transient scene-brush contents ignore it.
- [x] Document ownership and lifetime rules on the `DrawingRecording` XML
      doc comments.
- [x] **Immutable resource policy** — `DrawingRecording.Create(record)`
      (no compositor) snapshots mutable brushes, pens and layer effects at
      record time; scene brushes (`VisualBrush`, `DrawingBrush`,
      `DrawingRecordingBrush`) are captured via an immutable content
      snapshot. Resources that cannot be snapshotted throw, as does any
      reference to a compositor-bound recording (directly or through a
      brush's content) — an immutable recording cannot retain or track
      compositor-bound state. Replay of immutable recordings on the render
      thread therefore never touches an `AvaloniaObject`.

#### Tests (as shipped)

- `DrawingRecordingTests.Bounds_Available_Immediately` /
  `CompositorBoundDrawingRecordingTests.Compositor_Bound_Bounds_Available_Before_Commit`
  — eager bounds in both modes, before any commit.
- `DrawingRecordingTests.GetBounds_{Identity,Translate,Scale,Rotate,Empty}` +
  `GetBounds_Rotate_Gives_Tight_Union_Of_Per_Item_Aabb` — per-item transformed
  AABB semantics; `CompositorBound...GetBounds_With_Matrix_Works_Before_Commit`.
- `DrawingRecordingTests.Immutable_Parent_Bounds_Include_Nested_Immutable_Child`
  / `CompositorBound...Parent_Bounds_Include_Nested_Compositor_Bound_Child_Before_Commit`
  — bounds propagate through nesting.
- `DrawingRecordingTests.DrawRecording_With_Matrix_{Translates,Scales}_Bounds`,
  `DrawRecording_With_Identity_Matrix_Matches_Unmatrixed`,
  `DrawRecording_With_Matrix_HitTest_Uses_Transformed_Bounds`, and
  `DrawRecording_With_Rotation_Has_Per_Item_Tight_Bounds` — the latter proves
  the matrix fuses into the recording node (per-item tight bounds rather than
  a transformed united AABB). Render: `DrawRecording_With_Matrix_Translates_Content`.
- `DrawingRecordingTests.Shared_Ownership_Does_Not_Dispose_Child`,
  `Owned_Ownership_{Disposes_Child_With_Parent,Disposes_Transitively,Handles_Double_Reference,With_Matrix_Overload}`,
  `Owned_Children_Disposed_When_Record_Delegate_Throws`.
- `CompositorBound...{Same_Recording_Drawn_Multiple_Times_Increments_RefCount,Compositor_Bound_Child_Shared_Across_Multiple_Parents,Parent_Retains_Compositor_Bound_Child_After_External_Dispose}`
  — refcounted retention.
- Immutable resource policy:
  `DrawingRecordingTests.Immutable_Create_Snapshots_{Mutable_Brush,Mutable_Pen,Scene_Brush}`,
  `Immutable_Create_Throws_On_Brush_That_Cannot_Be_Snapshotted`;
  `CompositorBound...Immutable_Create_Throws_On_{Compositor_Bound_Child,Scene_Brush_With_Compositor_Bound_Source}`.

---

### Phase 0.3 — Bounds-Change Signal (R7)

Adds a bounds-change signal for compositor-bound recordings.

> **R6 was withdrawn during implementation.** Element identity belongs in
> the SVG layer, not in the recording. See "DrawingRecording Evolution"
> for context.

#### Checklist

- [x] **R7** — `DrawingRecording.BoundsChanged : event EventHandler<Rect>`
      raised on the UI thread when, after a compositor commit, the
      observable `Bounds` differs from the previously-observed value.
      Subscribing on an immutable recording throws
      `InvalidOperationException`; on a disposed recording throws
      `ObjectDisposedException`. Lazy hook: the recording subscribes to
      `Compositor.AfterCommit` only while there is at least one handler.

#### Tests (as shipped)

- `CompositorBoundDrawingRecordingTests.BoundsChanged_Fires_When_Pen_Thickness_Animated`
  — a geometry-affecting mutation fires the event after commit. (Brush
  `Transform` does not affect drawn bounds; pen thickness is the
  representative growth case.)
- `CompositorBoundDrawingRecordingTests.BoundsChanged_Does_Not_Fire_When_Bounds_Stable`.
- `CompositorBound...BoundsChanged_{Throws_On_Immutable_Recording,Throws_On_Disposed_Recording,Unsubscribe_Stops_Notifications,Dispose_Cleans_Up_Subscription}`.

---

### Phase 0.4 — `DrawingRecordingBrush` (G1 / SVG gap 4.1)

Tile brush whose source is a `DrawingRecording`.

#### Checklist

- [x] `Avalonia.Media.DrawingRecordingBrush : TileBrush, ISceneBrush` with
      `Recording`; `SourceRect`, `DestinationRect`, `TileMode`, `Transform`
      come from the `TileBrush`/`Brush` base.
- [x] No immutable snapshot type — matches `DrawingBrush`'s shape. When the
      brush is captured by an *immutable* recording, the recording snapshots
      the brush's current content at record time instead (see the 0.2
      immutable resource policy). Changing `Recording` after the brush is
      referenced by a compositor does not refresh existing content
      (documented; matches `DrawingBrush.Drawing`).
- [x] Serialization through `RenderDataDrawingContext` — the brush carries a
      refcounted reference to the server-side render data
      (`CompositionRenderDataSceneBrushContent`).
- [x] Server-side brush implementation reuses
      `ServerCompositionSimpleContentBrush` (same machinery as
      `DrawingBrush`/`VisualBrush`): realizes the content into a tile and
      samples per the tile mode.
- [x] Recording sources by context: compositor-bound sources work for
      compositor use and transient (immediate) content; immutable
      recordings accept only immutable sources (a compositor-bound source
      throws at record time, since it could not be retained or tracked).

#### Tests (as shipped)

- `Avalonia.Skia.RenderTests.DrawingRecordingTests.DrawingRecordingBrush_TileMode_{None,FlipX,FlipY,FlipXY}`
  + `DrawingRecordingBrush_Tiles_Recording` — tile-mode goldens. The
  tile-mode tests draw the brush from inside an immutable recording, so they
  also cover the record-time content snapshot end to end (composited and
  immediate).
- `...DrawingRecordingBrush_SourceRect_Selects_Region` — source-rect crop +
  dest-rect scale golden.
- `DrawingRecordingBrushTests.Accepts_Compositor_Bound_Recording_From_Same_Compositor`
  — compositor-bound source stays usable for transient content; animated
  propagation rides the same change-tracking machinery as `DrawingBrush`
  (covered by `CompositorBoundDrawingRecordingTests`).
- `DrawingRecordingBrushTests.{SceneBrush_Content_Survives_Source_Disposal,SceneBrush_Content_Is_Null_When_Recording_Is_Disposed,Brush_Reusable_Across_Multiple_Content_Creations}`
  — disposal and reuse semantics.

---

### Phase 0.5 — Luminance Masks (G2 / SVG gap 4.2)

#### Checklist

- [x] `Avalonia.Media.MaskType { Alpha, Luminance }` enum.
- [x] `DrawingContext.PushOpacityMask(IBrush, Rect, MaskType)` overload;
      existing alpha-only overload forwards to this with `MaskType.Alpha`.
- [x] `RenderDataDrawingContext` records the mask type on the opacity-mask
      node.
- [x] `Avalonia.Platform.IDrawingContextImplWithLuminanceMask` probe
      interface (non-breaking addition to the backend surface).
- [x] Skia implementation: luminance-to-alpha `SKColorFilter.CreateLumaColor`
      applied to the mask layer; a backend that doesn't implement the probe
      logs a one-shot warning and falls back to alpha.

#### Tests (as shipped)

- `DrawingRecordingTests.PushOpacityMask_Default_Behaves_As_Alpha` — default
  behavior unchanged.
- `DrawingRecordingTests.PushOpacityMask_Luminance_{Preserves_Bounds,Hits_Unchanged_By_Type}`
  — mask type does not perturb bounds/hit-testing.
- `Avalonia.Skia.RenderTests.DrawingRecordingTests.PushOpacityMask_Luminance_Differs_From_Alpha`
  — golden comparing luminance vs alpha for the same gradient mask.
- (A `MaskType` serialization round-trip test is moot: the in-process batch
  transport passes node object references, so the property cannot be lost.)

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
- [x] Recording contexts snapshot `LayerOptions.Effect` (mutable effects
      → `ToImmutable()`; non-snapshottable effects throw), so recorded
      layer nodes never read a live `AvaloniaObject` at replay time.
      `LayerOptions.Bounds` is a sizing hint for the offscreen buffer, not
      a clip — documented on the struct; consumers needing a hard clip
      (e.g. SVG filter regions) push an explicit clip around the layer.
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
      `IOffsetEffect`, `ICompositeEffect`). When SVG Phase 5 (filters)
      needs primitives beyond blur and drop-shadow, they land as a
      recording-side commit/follow-up PR — not on the SVG branch.

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

**Branch:** Begin `feature/svg-rendering` stacked on
`feature/drawing-recording` — there is no need to wait for the Phase 0
merge; rebase onto `master` once Phase 0 lands. All subsequent phases
extend this branch.

**Goal:** Static SVGs with basic shapes render correctly into a
`DrawingRecording`. No engine changes.

### Checklist

- [x] Create `src/Avalonia.Svg/Avalonia.Svg.csproj` and wire into `Avalonia.slnx`.
- [x] Parser skeleton: `SvgDocument.Load(Stream)` / `Load(Uri)` / `Parse(string)`.
      The reader ignores DTDs, resolves no external entities, and skips
      foreign-namespace subtrees (editor metadata).
- [x] `XmlReader`-based element parser producing `SvgElement` tree (+ id map).
- [x] Attribute + inline-style resolver (no CSS cascade yet — presentation
      attributes and `style="..."` only; `style` declarations win, `inherit`
      keeps the inherited value, inheritance via value-copied style context).
- [x] Reference attributes resolve both plain `href` (SVG 2) and legacy
      `xlink:href` (SVG 1.1) at a single lookup point (`SvgElement.Href`).
- [x] Unit parser (`px`, `pt`, `pc`, `mm`, `cm`, `in`, `em`, `ex`, `%`,
      unitless) → DIPs, with per-axis percentage resolution.
- [x] Path-data (`d`) parser → `IGeometryContext`/`StreamGeometry`. Full
      command set incl. smooth reflection, juxtaposed arc flags, scientific
      notation, compressed numbers; malformed input renders its valid prefix
      per the SVG error rules.
- [x] Transform parser (`translate/rotate/scale/skewX/skewY/matrix`) →
      `Matrix` (right-most transform applies first).
- [x] `viewBox` + `preserveAspectRatio` → outer `Matrix` (all alignments,
      meet/slice).
- [x] Compiler emits `DrawRectangle`, `DrawEllipse`, `DrawLine`, `DrawGeometry`
      for `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon`, `path`;
      `defs`/`symbol`/paint-server containers are excluded from rendering;
      `display: none` prunes subtrees.
- [x] `rx` / `ry` support the SVG 2 `auto` keyword on `<rect>` and
      `<ellipse>` (each defaults to the other when `auto` or absent).
- [x] Solid fill/stroke via `ImmutableSolidColorBrush` / `ImmutablePen`,
      including `currentColor` and the SVG initial values (miter limit 4,
      butt caps); `url(#...)` paints parse as references for Phase 2.
      Named-color coverage comes from Avalonia's `KnownColors`, which lacks
      the CSS Color 4 additions (e.g. `rebeccapurple`) — such values fall
      back to the inherited paint per CSS error handling.
- [x] `fill-rule` and `stroke-dasharray` / `stroke-dashoffset` round-trip
      verification (gaps 4.5, 4.8) — unit level: `SetFillRule` emission and
      the user-unit → thickness-multiple dash conversion (incl. odd-list
      doubling) are asserted; pixel goldens follow with the render-test
      project.
- [x] `SvgImage : IImage` that owns the root `DrawingRecording` and draws it
      via a single fused `context.DrawRecording(recording, matrix)` under a
      dest-rect clip.

### Tests

All tests live under the SVG test assemblies — no additions to
`Avalonia.Base` tests.

- [x] `Avalonia.Svg.UnitTests` parser tests (`SvgPathParserTests`,
  `SvgTransformParserTests`, `SvgLengthTests`, `SvgViewBoxTests`,
  `SvgPaintTests`, `SvgDocumentTests`) — path data asserts the emitted
  geometry commands through a recording `IGeometryContext` sink (no render
  platform needed); covers malformed input (valid-prefix emission),
  scientific notation, compressed numbers, juxtaposed arc flags, implicit
  commands, relative vs absolute, smooth reflection, href dual lookup,
  foreign-namespace skipping, DTD ignoring and intrinsic-size rules.
- [x] `Avalonia.Svg.UnitTests/SvgCompilerTests` — per-shape behavior
  observed through public `DrawingRecording` `Bounds`/`HitTest` (rect,
  rounded rect via `ry`+auto `rx`, circle, ellipse incl. auto radius, line,
  group/shape transforms, inheritance, `display:none`, `defs` exclusion,
  `currentColor`, viewBox meet/scale, `SvgImage` size + scaled draw) plus
  pen-construction tests (SVG initial values, dash thickness-multiple
  conversion, odd-list doubling). Geometry-backed shapes (path/poly) are
  covered at the parser level; their pixel behavior lands with the render
  tests.
- [x] `Avalonia.Svg.RenderTests` — golden-image project reusing the shared
  `TestRenderHelper` (immediate + composited pipelines, RMSE diff against
  `tests/TestFiles/Svg/`). Seed suite of 8 scenes shipped: basic shapes,
  polygon/polyline, path with curves + arcs, fill-rule nonzero vs evenodd,
  stroke-dasharray (user-unit periods across widths + dashoffset), viewBox
  meet letterboxing, nested transforms, inheritance + `currentColor`.
  The dash and fill-rule scenes are the pixel-level halves of gaps 4.5/4.8.
- [ ] Expand the corpus with curated W3C / resvg cases as Phases 2+ add
  features (gradients, use/symbol, text, …).

---

## Phase 2 — Gradients, Clipping, `<use>` / `<symbol>` / `<defs>`

**Goal:** Recording reuse works; gradients and clipping render correctly.
Pure SVG compiler work — all recording primitives (R1–R5) already exist from
Phase 0.

### Checklist

- [x] Linear/radial gradient parsing → immutable brushes, incl. stops
      (offset clamp + monotonic ordering, `stop-opacity`, `currentColor`),
      spread methods, focal points, single-stop → solid, and `href` /
      `xlink:href` inheritance chains with cycle protection.
- [x] `gradientUnits="userSpaceOnUse"` vs `"objectBoundingBox"` +
      `gradientTransform` — compiler-side coordinate transform (gap 4.12).
      Unit systems map onto `RelativeUnit.Absolute`/`Relative` directly;
      an objectBoundingBox `gradientTransform` is conjugated with the
      shape's bounding-box scale so unit-space transforms are exact even
      on non-square boxes. Zero-area boxes disable the paint per spec.
- [x] `clipPath` parsing and `PushGeometryClip` emission (shape children
      with per-child transforms and `clip-rule`; `clipPathUnits`
      objectBoundingBox supported where the element's box is attribute-
      derivable — rect/circle/ellipse; geometry-backed shapes and groups
      take userSpaceOnUse clips).
- [x] `<defs>` resolution; `<symbol>` (and any `<use>` target) compiled once
      to a standalone `DrawingRecording`, cached on `SvgDocument` keyed by
      (element, viewport). Shared content compiles with the default style
      context — use-site style inheritance into unstyled referenced content
      is not propagated (the recording is shared); self-styled content (the
      common case) is unaffected.
- [x] `<use href>` emits a single fused `DrawRecording(rec, transform,
      DrawingRecordingOwnership.Shared)` call at the use site. `x`/`y`,
      `transform`, `opacity` and symbol viewport mapping (width/height,
      viewBox + preserveAspectRatio, overflow clip) apply at the use site
      via push states around the call; reference cycles are pruned per the
      SVG error rules.
- [x] `SvgDocument.Dispose` releases the cached shared sub-recordings;
      already-compiled content keeps replaying through its `Shared`
      references.
- [x] `SvgImage.ContentBounds` / `GetContentBounds(Matrix)` expose the
      recording's eager bounds and R3 per-item bounds for precise layout
      measurement (the viewBox mapping is baked into the recording).

### Tests (as shipped)

- [x] `Avalonia.Svg.UnitTests/SvgGradientTests` (17) — resolver-level: both
  unit systems, defaults, spread methods, stop clamp/monotonic/opacity/
  `currentColor`, single-stop solid, no-stops null, `href` inheritance +
  cycle safety, userSpace and conjugated objectBoundingBox
  `gradientTransform`, zero-area box, focal points, unknown references.
- [x] `Avalonia.Svg.UnitTests/SvgUseSymbolTests` (11) — observed through
  recording bounds/hit-testing: use translation, xlink fallback, direct
  duplication, symbol viewBox scaling + overflow clipping, one shared
  recording per target (`SharedRecordingCount`), nested use, cycle pruning,
  missing target, document disposal releasing the cache while compiled
  content keeps replaying.
- [x] `Avalonia.Svg.RenderTests` goldens (10) — linear oBB, conjugated
  rotate `gradientTransform` on a non-square box, three spread methods,
  radial focal offset, stop-opacity over background, `href` inheritance;
  symbol reused at three sizes with an opacity override, use-of-group with
  transforms; circle clipPath over a group, objectBoundingBox clipPath.
- [ ] Curated W3C / resvg gradient + clipPath + use corpus cases as the
  suite grows.

---

## Phase 3 — Text, Markers, Paint Order

**Goal:** Text content (including text-on-path), SVG arrow markers, and
`paint-order` all render correctly. All changes compiler-side; no engine
changes.

### Checklist

- [x] `<text>` / `<tspan>` layout: resolve font family/size/weight/style to
      `Typeface`; lay out through **`TextLayout`** (an `ITextSource` with
      per-run properties), not direct `TextShaper`/`GlyphRun` construction —
      SVG text must go through the full pipeline (script itemization, font
      fallback, bidi) to stay on par with regular Avalonia text rendering.
      Chunks split into layout segments at `dx`/`dy` adjustments; style-only
      `tspan` boundaries shape continuously within one layout. The parser
      captures mixed element/text content in document order with SVG
      white-space normalization.
- [x] `text-anchor`, `dx`/`dy`, basic `x`/`y` offsets (scalar values;
      per-glyph position lists are out of scope). SVG baselines map to
      `TextLayout` origins via the first line's baseline; absolutely
      positioned `tspan`s start new anchor chunks.
- [x] `<textPath>` — arc-length sample the referenced path (gap 4.6 option
      (a)) via a path flattener; the text lays out through `TextLayout`
      (fallback-correct), then each glyph is placed individually:
      one fused `PushTransform` + `DrawGlyphRun` per glyph, rotated to the
      tangent, with `startOffset` (incl. percentages) honored and glyphs
      past the path end dropped per spec.
- [x] `<marker>` — each marker compiles once to a shared `DrawingRecording`;
      vertex positions + in/out tangents come from the path sampler (lines
      and polylines/polygons compute them directly); every placement is a
      single fused `DrawRecording(markerRec, matrix, Shared)` combining
      viewBox fit, refX/refY alignment, `markerUnits` stroke-width scaling
      and orientation (gap 4.7). `orient` supports `auto`, fixed angles,
      and SVG 2 `auto-start-reverse`; interior vertices bisect in/out.
- [x] `paint-order: stroke fill` — split into two draw calls per shape
      (geometry, rect and ellipse paths; gap 4.4). Markers always paint
      last.
- [x] `mix-blend-mode` / `isolation` →
      `PushLayer(new LayerOptions { BlendMode = …, Isolate = … })` around
      the element or group. Landed `LayerOptions.Isolate` on the recording
      side first (the passthrough elision left no way to force a bare
      isolation layer); element opacity, blend mode and isolation fold into
      one recorded layer.
- [x] `fill-opacity`, `stroke-opacity`, and `opacity` composition rules:
      the paint opacities multiply into the brushes; element `opacity`
      uses the layer API for correct group semantics (overlapping children
      composite before the group fades); `opacity: 0` prunes the subtree.

### Tests

Tests, as shipped:

- [x] `SvgPathSamplerTests` (7) — arc-length measurement (lines, arcs),
  point/angle sampling, vertex tangents incl. closed-figure bisection,
  cubic control-point tangents, malformed-prefix sampling.
- [x] `SvgMarkerTests` (7) — placement and orientation observed through
  recording hit-testing: end/mid markers, `markerUnits` strokeWidth vs
  userSpaceOnUse, `auto` rotation, `auto-start-reverse` flip, refX/refY +
  viewBox alignment, one shared recording per marker.
- [x] `SvgCompositingTests` (9) — group opacity bounds, `opacity: 0`
  pruning, blend/isolation bounds, mask bounds + shared recording,
  pattern brush construction (immutable scene-brush content, dest/source
  rects per unit system, href chains, empty/zero rejection), fill-opacity
  in the brush.
- [x] Render goldens (14): text anchors (pixel-measured: the middle anchor
  centers exactly), styled `tspan`s with `dy`, text-on-path along an arc;
  markers on a polyline (auto) and `auto-start-reverse` arrows on a curve;
  group-vs-fill opacity, multiply with and without isolation, paint-order
  stroke-first; oBB checkerboard pattern (per-shape tile scaling),
  user-space pattern with `patternTransform`, viewBox pattern; luminance
  gradient mask, `mask-type="alpha"`, mask on a group (measured fill box).
  Pixel verification of glyph positions confirms the embedded-font resm
  resolution; text scenes use the deterministic Noto Mono test font.
- [ ] Curated W3C / resvg text + marker corpus cases as the suite grows.
  Stroked text and `text-decoration` are deferred (noted for a later
  pass alongside per-glyph position lists).

---

## Phase 4 — Patterns & Masks

**Goal:** SVG `<pattern>` and `<mask>` compile correctly onto the
`DrawingRecordingBrush` and `MaskType.Luminance`/`Alpha` APIs already shipped
in Phase 0 (sub-phases 0.4 and 0.5). Pure SVG compiler work.

### Checklist

- [x] SVG `<pattern>` → compile contents to a cached sub-`DrawingRecording`
      and wrap in a `DrawingRecordingBrush` with the pattern's `x`, `y`,
      `width`, `height`, `patternTransform`, `patternUnits`,
      `patternContentUnits` and `viewBox` mapped onto
      `SourceRect`/`DestinationRect`/`Transform`. The mutable brush is
      snapshotted via `ToImmutable()` (the Phase 0 scene-brush snapshot)
      so immutable recordings capture it safely.
- [x] Reference resolution: `<pattern href="#other">` inheritance chain
      (attributes and content, cycle-safe).
- [x] SVG `<mask>` → compile contents to a shared sub-recording, wrap in an
      immutable content brush mapped 1:1 over the mask region, emit
      `PushOpacityMask(brush, region, MaskType.Luminance)` by default,
      `MaskType.Alpha` when `mask-type="alpha"`. Mask regions honor the
      spec's -10%/120% defaults; group masks measure the element fill box
      through a throwaway strokeless recording.
- [x] Unit-system conversions (`userSpaceOnUse` vs `objectBoundingBox`) for
      both pattern (tile and content units independently) and mask (region
      and content units).

### Tests

Tests, as shipped (see also the Phase 3 list — patterns/masks share the
`SvgCompositingTests` unit coverage and the `PatternsAndMasks` goldens):

- [x] Unit: pattern brush construction per unit system, viewBox source
  mapping, href inheritance, empty/zero-area rejection; mask bounds and
  shared-recording reuse.
- [x] Render goldens: oBB checkerboard (per-shape tile scaling proves the
  bounding-box mapping), user-space stripes under `patternTransform`,
  viewBox-scaled dots; luminance gradient fade, `mask-type="alpha"`
  (a luminance-dark but opaque mask passing at full strength proves the
  mode switch), mask on a group.
- [ ] Curated W3C / resvg pattern + mask corpus cases as the suite grows.

---

## Phase 5 — Filter Effects

**Goal:** SVG `<filter>` compiles to
`PushLayer(new LayerOptions { Bounds = filterRegion, Effect = composedEffect })`
using the layer API already shipped in Phase 0.6. Pure SVG compiler work.
The new `IEffect` subtypes that Phase 0.6 deferred are a **recording-side
prerequisite**: they land as a commit/follow-up PR on the recording side
(consistent with the "no `Avalonia.Base` additions from the SVG branch"
rule) before this phase's compiler work consumes them.

### Checklist

- [ ] SVG `<filter>` region (`x`, `y`, `width`, `height`,
      `filterUnits="userSpaceOnUse"` vs `"objectBoundingBox"`) →
      `LayerOptions.Bounds` **plus an explicit `PushClip(filterRegion)`**
      around the layer — `LayerOptions.Bounds` sizes the offscreen buffer
      but does not clip, while SVG's filter region is a hard clip.
- [ ] **Prerequisite (recording side, not this branch):** new `IEffect`
      subtypes (deferred from Phase 0.6) with their `Immutable…Effect`
      counterparts: `IColorMatrixEffect`, `IOffsetEffect`,
      `ICompositeEffect` (linear chain). Skia lowering via
      `SKColorFilter.CreateColorMatrix`, `SKImageFilter.CreateOffset`,
      `SKImageFilter.CreateCompose`. `EffectAnimator` registration so each
      new effect's parameters participate in transitions, and
      `RenderDataLayerNode` bounds inflation extended for offset (shifts
      bounds) and composite (union of stage paddings).
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
- [ ] Animation driver, two channels (recorded transforms and geometry are
      immutable values — only paint resources are mutable inside a
      recording):
  - [ ] **Paint animation** (`fill`, `stroke`, pen thickness, brush
        transforms): mutable brushes/pens recorded into a compositor-bound
        recording; the compositor's change tracking propagates mutations
        without re-recording. Layout reacts via R7 `BoundsChanged`.
  - [ ] **Structural animation** (`animateTransform` on elements/groups,
        `d` morphing, structural attribute changes): re-emitted at the
        control level — the `Svg` control invalidates and re-issues cheap
        `DrawRecording(subRecording, matrix, Shared)` calls against the
        unchanged sub-recordings (the same mechanism as visibility
        toggling). Morphing shapes draw their geometry directly or swap a
        small per-shape recording instead of being baked into a shared
        recording.
- [ ] Animation targets resolve by element id through the
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
  parent until the parent is disposed; lifetime is refcounted client-side
  (UI thread), releasing the server data on the next batch. Default
  ownership on the existing `DrawRecording(rec)` becomes `Shared`
  (R2, R5, Phase 0.2).
- `DrawRecording(rec, matrix)` records one fused node instead of a
  transform push around the recording node (R4, Phase 0.2).
- Immutable `Create(record)` snapshots mutable brushes/pens/effects at
  record time (scene brushes via their current content) and throws for
  resources that cannot be snapshotted or that reference compositor-bound
  state. `Owned` children are disposed even when the record delegate
  throws. (Phase 0.2 immutable resource policy.) Snapshotting rides the
  existing `BrushExtensions.ToImmutable` (brush/pen/dash-style) and
  `EffectExtesions.ToImmutable` helpers; the recording context only adds
  the embed-policy checks.
- `BrushExtensions.ToImmutable(IBrush)` — public behavior change — now
  snapshots scene brushes (`VisualBrush`, `DrawingBrush`,
  `DrawingRecordingBrush`) to an immutable snapshot of their current
  content (transparent brush when there is no content) instead of throwing
  `InvalidCastException`. `Pen.ToImmutable` and the composition visual's
  `OpacityMask` serialization inherit this.

### Added by Phases 1–6 (`feature/svg-rendering`)

```
Avalonia.Svg
├── SvgDocument           // Load/Parse; owns recordings; IDisposable.
├── SvgImage : IImage     // IImage implementation that draws the recording.
├── Svg : Control         // DPs: Source (Uri/Stream/string), Stretch,
│                         //      StretchDirection; raises hit events.
└── SvgSource             // Markup-extension friendly, cacheable.
```

**No additions to `Avalonia.Base` from the SVG branch.** Phases 1–6 consume
the recording API exclusively. If a gap is discovered, the fix lands on the
recording side (a commit or follow-up PR on `feature/drawing-recording`, or
against `master` once it merges) and the SVG branch rebases onto it — never
mixed into the SVG branch itself. The Phase 5 effect subtypes are the one
already-known instance of this rule.

---

## Cross-Cutting Test Infrastructure

- **Golden-image corpus.** Curate a subset of the W3C SVG 1.1 test suite under
  `tests/Avalonia.Svg.RenderTests/Golden/`. Each `.svg` has a reference PNG
  rendered at a fixed DPI; tests diff with a PSNR/SSIM threshold. Augment
  with cases from the resvg test suite — the maintained corpus for exactly
  the Spec Target's "1.1 + de-facto SVG 2" profile — covering the cherry-
  picks (plain `href`, `paint-order`, `mask-type`, blend modes,
  `auto-start-reverse` markers, `rx`/`ry` `auto`).
- **Compiler snapshot tests.** Serialize emitted `RenderItemList` to a stable
  textual representation; compare to checked-in snapshots (like
  `DrawingRecordingTests`).
- **CI stability.** Render tests use a headless Skia backend (see existing
  `RenderTests` project) to keep output deterministic across platforms.
- **Benchmarks.** `BenchmarkDotNet` suite under `tests/Avalonia.Svg.Benchmarks`
  for parse → compile → first-frame on a representative set (logos, icons,
  maps, chart SVGs). Track regressions in CI.
