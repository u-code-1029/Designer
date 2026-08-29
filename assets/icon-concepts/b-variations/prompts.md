# DrillFlow Designer — B-family icon variation prompts

All four final PNG files were created with the built-in image generation tool using the existing B icon as a style and concept reference.

## B1 — compact loop controller

```text
Use case: logo-brand
Asset type: Windows desktop application icon concept, square transparent PNG
Input images: Image 1 is a style and concept reference only; create a new distinct composition rather than editing or tracing it.
Primary request: Create a compact loop controller mark in the same visual family as Image 1. One thick teal-to-azure command ribbon flows as a rounded-square loop that wraps around exactly two horizontal control sliders. A single small amber control node anchors the center of the composition.
Style/medium: minimal vector-friendly Fluent UI icon, matte semi-flat finish, smooth restrained gradients, crisp anti-aliased edges.
Composition/framing: centered, dense and nearly square silhouette that is clearly different from the tall S-shaped reference; only 2 to 4 large bold forms; balanced generous transparent margin; designed to remain recognizable at 16 px.
Color palette: preserve deep navy slider rails, teal-to-azure command ribbon, and one restrained amber accent node from the reference.
Constraints: genuine transparent RGBA background; a single isolated icon mark; strong simple silhouette; no text, letters, numbers, watermark, checkerboard, tile, background panel, gloss, bevel, cast shadow, 3D extrusion, or tiny details.
```

## B2 — bidirectional controller

```text
Use case: logo-brand
Asset type: square Windows desktop application icon concept, transparent PNG
Primary request: Generate a new sibling variation inspired by Image 1, not an edit or copy. Create a compact bidirectional command controller mark: two thick, rounded teal-to-blue command ribbons enter symmetrically from the left and right and meet at one central amber control hub. Behind them, place exactly two simple horizontal navy slider rails with restrained circular controls. The mark should imply request/response communication and precise equipment adjustment without relying on multiple arrowheads.
Input image: Image 1 is a style and concept reference only. Preserve its matte semi-flat Fluent UI character, teal/cyan/blue gradient family, dark navy structural rails, and warm amber focal control. Do not reproduce its exact path.
Scene/backdrop: genuinely transparent RGBA background; no colored or black backdrop.
Style/medium: polished minimal Fluent UI app icon, vector-friendly raster rendering, matte semi-flat shapes, crisp clean silhouette, gentle color transitions only.
Composition/framing: nearly bilateral symmetry, centered compact mark, generous transparent margin, 2 to 4 dominant thick shapes, readable at 16 px, balanced negative space.
Color palette: teal and cyan flowing into vivid blue, deep navy rails, one amber/yellow central hub.
Constraints: actual alpha transparency; strong recognizable silhouette; smooth rounded geometry; only one central amber hub; exactly two background rails; avoid visual clutter.
Avoid: text, letters, numbers, watermark, checkerboard, background tile, excessive arrows, repeated arrowheads, thin lines, tiny details, gloss, bevel, drop shadow, extrusion, 3D, photorealism.
```

The first B2 output drew a checkerboard, so this targeted background correction produced the final file:

```text
Use case: background-extraction
Asset type: final transparent Windows desktop application icon concept
Input image: Image 1 is the exact edit target. Preserve the icon mark itself pixel-for-pixel in visual design.
Primary request: Remove only the entire white and light-gray checkerboard background and replace it with genuine alpha transparency.
Invariants: keep the bidirectional command ribbon, both navy slider rails, circular rail controls, central navy ring and amber hub exactly unchanged in shape, proportions, position, spacing, color gradients, edge quality, and matte semi-flat Fluent UI style. Do not redesign, simplify, add, remove, rotate, resize, recolor, or move any part of the mark.
Composition/framing: preserve the current square canvas, centered mark, and generous margin.
Constraints: every pixel outside the mark and inside its intended holes must have alpha 0. Return a true 32-bit RGBA PNG with genuine transparency.
Avoid: drawn or simulated checkerboard, white background, gray background, colored background, tile, halo, shadow, text, letters, numbers, watermark, gloss, bevel, new detail, or any change to the icon mark.
```

## B3 — modular control path

```text
Use case: logo-brand
Asset type: square Windows desktop application icon concept
Input images: Image 1 is a style and concept reference only; generate a new, distinct mark rather than editing or tracing it.
Primary request: Create a modular control-path symbol for an industrial equipment workflow designer. Preserve the reference's matte semi-flat Fluent visual language, deep navy control rails, and smooth teal-to-azure flowing-path gradient. A single thick continuous path connects exactly three large circular control nodes in a compact diagonal stair-step arrangement from upper-left to lower-right. From each circular node, extend one short horizontal slider rail so workflow sequence and parameter adjustment read simultaneously. Use at most one subtle endpoint arrow.
Composition/framing: centered compact diagonal silhouette, balanced square footprint, generous transparent margin, strong negative space, only 2–4 bold visual groups. Every major feature must remain recognizable at 16×16 pixels.
Style/medium: clean vector-friendly Fluent UI app icon, matte semi-flat surfaces, crisp edges, restrained depth from color layering only.
Color palette: dark navy rails and node rims; flowing path transitions from teal through cyan to azure; at most one tiny warm amber control accent if needed.
Background: genuinely transparent RGBA alpha; no backdrop or canvas.
Constraints: original design; exactly three large circular control nodes; thick forms; clear silhouette; no text, letters, numbers, watermark, checkerboard, tile, border, frame, gloss, bevel, cast shadow, 3D extrusion, photorealism, or tiny details. Do not include more than one arrow.
```

## B4 — signal dial path

```text
Use case: logo-brand
Asset type: additional Windows desktop app icon concept for DrillFlow Designer
Input images: Image 1 is a style and concept reference, not an edit target.
Primary request: Create a new B-family variation called “signal dial path.” Preserve Image 1's visual language of a flowing command path, adjustable controls, and restrained Fluent gradient, but use a clearly different composition: one thick teal-to-azure ribbon flows vertically through three large circular control dials arranged in a compact triangular layout, then turns into one subtle outgoing endpoint. The dials and their short supports use deep navy; one small warm amber center marks the active control.
Style/medium: minimal matte semi-flat Fluent UI vector-like icon; rounded geometric forms; crisp edges; only 2–4 bold visual groups; highly legible at 16 px.
Composition/framing: centered compact square-friendly mark, balanced negative space, generous transparent margin, strong silhouette distinct from the original horizontal three-rail S path.
Color palette: restrained teal-to-azure gradient, deep navy controls, one small amber accent.
Scene/backdrop: genuinely transparent RGBA alpha background, no tile or container.
Constraints: no text, letters, numbers, watermark, checkerboard, white/gray background, mockup, extra arrows, gloss, specular highlight, reflection, glass, chrome, bevel, extrusion, inner shadow, bloom, 3D depth, or tiny decorative details. Keep surfaces matte and edges clean.
```
