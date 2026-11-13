# Server-side composition visuals

`ServerCompositionVisual` is the render-thread side of `CompositionVisual`. The files here hold the two per-frame tree walks (collecting the dirty region, then drawing it), bitmap caches, adorners, readback for hit-testing, and backdrop effects.

## Model

- A visual stores its own transform (to parent space) and its subtree bounds, never a world transform. Each walk keeps the transform to the current layer on a stack as it descends.
- A change marks the visual and sets flags up its parent chain (`PropagateFlags`). The update walk only descends where flags are set, so a frame costs what changed, not what exists.
- The render walk skips any subtree whose bounds miss the dirty region or the clip.
- A bitmap cache draws its subtree into a surface of its own with a dirty region of its own, following WPF's `BitmapCache`.

## Glossary

Comments in these files use the first term. Older words, still found in comments and in some identifiers, are in parentheses.

### Tree

- **visual** ('node'): a `ServerCompositionVisual`.
- **subtree** ('subgraph'): a visual and everything below it.
- **parent chain** ('ancestors'): every parent up to the root.
- **local space** ('inner space'): the visual's coordinate space before its own transform. **Parent space** ('outer space') is after it.
- **bounds** ('bbox'): an extent in a stated space. `_ownContentBounds` and `_subTreeBounds` are local, `_transformedSubTreeBounds` is in parent space.
- **draw order** ('DFS order', walk position): the order the render walk draws visuals in. Earlier is **behind**, later is **in front**.

### Passes

- **update walk**: `UpdateContext`. Recomputes subtree bounds and collects every layer's dirty region.
- **backdrop pass**: `ServerCompositionTarget.ExpandDirtyRegionForBackdrops`. Runs after the update walk and widens the dirty region for backdrop visuals.
- **render walk**: `RenderContext`. Draws what intersects the dirty region.
- **readback pass**: publishes the transforms the UI thread uses for hit-testing.

### Layers

- **layer** ('host'): what a subtree is drawn into.
  - **root layer**: what the `ServerCompositionTarget` draws into: the render target, an intermediate surface, or a recording context with no pixels.
  - **bitmap cache layer**: the surface of a bitmap cache.
- **intermediate layer** ('save-layer'): a temporary layer the drawing backend opens for opacity, an effect or an opacity mask. Never just "layer".
- **layer space** ('host space'): device pixels for the root layer, the cached visual's local space for a bitmap cache layer. `TryGetHostBounds` resolves bounds in it.
- **bitmap cache** ('cache'): `ServerCompositionVisualCache`. Never just "cache": backdrops retain things too.

### Dirty region

- **dirty rect**: one rectangle to redraw.
- **dirty region** ('damage'): all dirty rects of one layer this frame.
- **working set**: the dirty rects the update walk has collected so far for the current layer, read without finalizing (`DirtyRectWorkingSet`).
- **collect** ('emit', 'add'): add a dirty rect to a dirty region.

### Backdrops

- **backdrop**: the pixels already in the layer behind a visual when it is drawn.
- **backdrop visual** ('glass'): a visual with a `BackdropEffect`. The effect fills its backdrop region, then the visual's content and children draw on top.
- **backdrop region**: what the effect fills, in local space (`BackdropRegion`): the layout rect or the subtree bounds per `BackdropEffectBoundsSource`, shaped by `BackdropClip`.
- **read reach** ('reach'): how far, per side, the effect reads past an output pixel (`GetEffectReadReach`). **Input area** is the region inflated by it.
- **influence**: how far, per side, a changed input pixel can move the output (`GetEffectInfluence`); the read reach mirrors it. Neither is the output padding: erosion and lighting pad nothing but read neighbors, and an offset pads the side it moves toward while reading from the other.
- **pointwise**: an effect whose output pixel reads only the input pixel beneath it (`IsPointwise`). It needs no reach and no retention.
- **classification** ('capture'): the update walk records each backdrop visual at its draw-order position (`BackdropCapture`). Dirty rects already in the working set there are behind it and change its input; later ones are in front and do not. Backdrop visuals the walk never reached are classified afterwards from a journal of dirty rects and the visuals that collected them.
- **payload**: what a backdrop visual retains between frames (`BackdropCachePayload`): its filtered **output**, its unfiltered **input**, or **none**.
- **refresh grant** ('grant'): the backdrop pass's statement that this frame's dirty region covers everything a refresh reads. Full, or partial with sub-rects (`RefreshRects` for output, `IngestRects` for input).
- **cache slot**: `BackdropLayerCache`, the handshake between the backdrop pass (policy) and the drawing backend (mechanism).

## Frame pipeline

1. **Apply batches.** Deserialize what the UI thread committed. A visual leaving its parent adds its old bounds there as an extra dirty rect. Unregistering a backdrop visual releases its retained payload here, with the graphics context made current.
2. **Own properties pass.** Recompute own transforms, clips and content bounds, then propagate flags.
3. **Adorner pass.** Own transforms are final now, so adorners can follow their adorned visual.
4. **Update walk.** Recompute bounds, collect dirty regions, classify every visited backdrop visual at its position. Under a pushed bitmap cache, dirty rects go to that cache's dirty region and the cached visual's bounds become dirty in the outer layer.
5. **Backdrop pass.** Per layer, deepest bitmap cache first: apply payload switches decided last frame, then for every backdrop visual touched by the working set decide between drawing its retained payload, a partial refresh, or a full refresh, and widen the dirty region to cover what that refresh reads. Widening one backdrop visual's region can touch another painted later, so the pass repeats until it adds nothing. Damage in a bitmap cache layer reaches the outer layer as the cached visual's bounds.
6. **Render walk.** Draw the dirty region. A backdrop visual pushes a layer whose backdrop is its effect; the backend draws the retained payload when the slot is valid and ungranted, refreshes it when granted, and otherwise samples the layer live. A visual with a bitmap cache draws its own backdrop on the outer layer, then the cache texture.
7. **Readback pass.**

## Backdrop rules

- The filter runs only on a frame whose dirty region covers its whole input. Anything left unpainted holds last frame's output, which already contains the backdrop, and filtering it again smears. Every shortcut below keeps this.
- The effect reads real layer content up to its reach past the region, so content just outside tints the glass edge. That ring is part of the input area and is tracked like the rest of it.
- A dirty rect in front of a backdrop visual never invalidates its retained payload.
- Retention never changes pixels. A backdrop visual under an intermediate layer, with its own opacity mask, or on a root layer without a readable surface, retains nothing and samples live; it still renders.
- A pointwise effect sampled live widens nothing: the redrawn slice is its own input.
- Each layer has its own backdrop. A backdrop visual inside a bitmap cache reads that cache's layer; the cached visual's own backdrop reads the outer one.
- A subtree-bounded backdrop visual whose subtree bounds may change is redrawn as a whole.
