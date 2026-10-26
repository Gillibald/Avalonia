# TextTour

A presenter-driven walk of one string through Avalonia's text pipeline: Unicode units, font resolution, tokenization, layout and hit testing. Every view is drawn from the real pipeline (`TextLayout`, `TextLine`, `ShapedTextRun`/`ShapedBuffer`, the Unicode enumerators, `FontManager`, and the formatter's internal `BidiAlgorithm` through `InternalsVisibleTo`). The app explains; it does not measure. Rasterization stays in TextLab and TextShowcase.

```
dotnet run --project samples/TextTour/TextTour.csproj
```

## Chapters

| Key | Chapter | Steps |
| --- | --- | --- |
| 1 | Start | the string in a plain `TextBlock`; the map of the stages |
| 2 | Units | chars (UTF-16 code units) with hex values; code points spanning their units; grapheme clusters spanning their code points; the glyphs shaping produced, spanning their clusters; counts per row. Hover a code point for its Unicode property card. |
| 3 | Fonts | the family list as a query (weight, style, stretch, culture); the collection asked for each name; the family's faces ranked by CSS matching distance with FontManager's answer highlighted; synthesized bold or oblique; the string coloured by the face of each shaped item, with the fallback request (`FontManager.TryMatchCharacter`) per uncovered item |
| 4 | Tokens | bands under the characters in logical order: runs from the text source (the preset has a span with its own brush), graphemes, words, break opportunities (solid mandatory, dashed allowed), BiDi levels per code point, shaped items (script, level, face); then the glyphs in visual order above, each connected to its cluster. A dashed red line ties a glyph to its own code point by cmap, which shows the Devanagari vowel sign drawn before its consonants. Hover a glyph for its id, cluster, advance and offset. |
| 5 | Layout | the string wrapped at a width (slider at the top, or drag the dashed edge); break opportunities with the chosen break per line; runs animating from logical into visual order (V); line metrics (hover a line); a second font with other vertical metrics; trimming modes and a right-to-left line with an ellipsis |
| 6 | Hits | click for the `TextHitTestResult`; caret stops as ticks (Left/Right walk them); the ffi ligature and its caret stops; the two caret positions of one text position at a BiDi boundary; drag to select with the `HitTestTextRange` rectangles |
| D | Display | window size in points and pixels, render scaling, screens, a text size ladder and colour swatches for checking the projector |

The bottom bar names the type and member behind the current step and shows the step counter.

## Keys

| Key | Action |
| --- | --- |
| PageDown, Space, Right | next step (past the last step: next chapter) |
| PageUp, Left | previous step |
| 1-6, D | chapter; D is the display test page |
| `[` `]` | previous or next chapter |
| Home, End | first or last step |
| T | light or dark theme |
| F or F11 | full screen; Escape leaves full screen |
| `+` `-` | content scale, 75 to 200 % |
| R | hide or show the chapter rail |
| I | edit the string in the input box; Escape leaves the input box |
| V | Layout step 3: toggle logical and visual run order |

Keys work whatever has focus, except while typing in the input box or the family box (only PageUp, PageDown and Escape pass through). No Ctrl+arrow combinations, which switch Spaces on macOS. In Hits steps 2 and 3, Left and Right move the caret instead of stepping; PageDown and Space still step.

## Presets

| Preset | Shows |
| --- | --- |
| Running example | `Office ffi` + Arabic word with a number + Devanagari conjunct with i-matra + family emoji (ZWJ) + two CJK ideographs |
| Latin only | ligatures, precomposed and combining accent |
| Arabic | joining forms, a number inside right-to-left text, mirrored brackets |
| Devanagari | conjuncts and the i-matra drawn before its consonants |
| Emoji | ZWJ sequence, skin tone modifier, flag, variation selector, keycap |
| CJK | breaks between ideographs, never before a closing bracket |

Each preset carries a style span (a run with its own brush) that the Tokens chapter shows as a separate source run. Typing in the input box drops the span.

## Headless export

```
TextTour --export-steps <dir> [--size 1920x1080] [--theme light|dark] [--preset <name>] [--scale <percent>] [--culture <name>]
```

Renders every chapter, step and demo variant to `<dir>/NN-chapter-step[-variant].png` and exits. `export.txt` next to the images lists the renderer, the caption of every frame and a plain text analysis of the string (units, breaks, levels, source runs, items, glyphs). It also says whether a fresh layout after all chapters still matches the first one, which catches lookups that changed the pipeline's answers.

The export hosts the shell at the requested size in a small offscreen window and captures it with `RenderTargetBitmap` at 96 dpi, using the platform's own font manager and the Skia render interface. Offscreen surfaces get grayscale antialiasing, so the PNGs differ from the live window in antialiasing and, on the Mac, in the fonts available. Demo variants (a clicked hit, a hovered card, a font query) are set by the chapter, not by input.

## macOS notes

- The default family is the system default (SF Pro through the CoreText provider); fallback picks Geeza Pro or SF Arabic, Kohinoor Devanagari, Apple Color Emoji and PingFang or Hiragino by itself.
- Named families for demo moves are picked at runtime from what the system lists: Avenir Next (Segoe UI on Windows) for a real bold italic, PingFang SC (Microsoft YaHei) for an italic the family lacks, Calibri / Hoefler Text / Baskerville for the ffi ligature when the current font has none, Gabriola / Zapfino / Snell Roundhand for a font with other vertical metrics.
- The Fonts chapter's culture box switches the culture of the text runs; on Windows the CJK pair falls back to Yu Gothic UI, Microsoft YaHei UI, Microsoft JhengHei UI or Malgun Gothic for ja-JP, zh-CN, zh-TW and ko-KR. Whether the CoreText provider honours the culture is a check for the Mac.
- Use the display test page (D) to read the effective size and render scaling on the projector, and the content scale (`+`/`-`) when the room is large.
