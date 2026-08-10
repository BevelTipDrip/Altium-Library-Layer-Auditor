# Layer Auditor — Session Findings & Project Status

Snapshot written because context ran low. Purpose: let a future session (or a
human) pick this up cold — what the app does today, what we learned about the
`.PcbLib` format along the way, how the git/fork infrastructure is laid out, and
exactly where the "accurate 3D body projection" work stands (researched, not yet
built).

See also: **`ALTIUM-PCBLIB-FORMAT-NOTES.md`** — the canonical, detailed writeup of
every reverse-engineered binary-format finding (layer numbering, mechanical >16
encoding, layer enabled/disabled gating, `StandoffHeight` sign, text justification
+ `IsJustificationValid`, the SmartUnion trap). This file summarizes those and adds
everything since that doesn't belong there (app features, git/fork setup, the 3D
projection research). Read that file for full mechanics; read this one for the
big picture and what's next.

---

## 1. What the app does today (PcbLibViewer)

A drag-and-drop web app (ASP.NET Core minimal API backend in `Program.cs`, vanilla
JS SPA in `wwwroot/index.html`) for auditing and fixing Altium `.PcbLib` footprint
libraries.

### Layer display
- Shows every layer a footprint uses, with correct names read from the library's
  own layer-stack header (not assumed defaults) and correct Altium-facing layer
  numbers in brackets, including mechanical layers past 16 (no fixed cap, verified
  live up to Mechanical 89).
- The "Legal Layers" checklist and the per-footprint "Layers" panel only show
  layers actually **enabled** in the library — not just named (Altium's binary
  format writes a full name template for every possible layer slot regardless of
  whether it's turned on; see format-notes §4). Collapsible groups (Standard /
  Mechanical / "Defined, unused on this footprint") via native `<details>`.

### Auditing
- **Illegal-layer report**: user marks which layers are "legal" per-library
  (mechanical defaults illegal, standard defaults legal); reports every
  primitive/3D-body on an illegal layer, grouped by footprint/layer/kind.
- **Content warnings** (separate, yellow-flagged track — a layer can be legal but
  still wrong): stray primitives on the Top/Bottom 3D Body layer, a 3D body
  missing entirely, a 3D body on the wrong layer, empty Courtyard/Assembly. These
  are **mount-side aware** (`Program.cs`'s `MountSide`) — a normal top-only SMD
  part is never flagged for an empty Bottom Courtyard; through-hole/mid-mount
  parts (detected via pad `HoleSize > 0` OR a 3D body with negative
  `StandoffHeight` — see format-notes §6) get relaxed "either side is fine, but
  not neither" treatment instead.

### Reassignment (fixing illegal/misplaced content)
- **Bulk**: move everything of a Kind ("Primitive"/"3D Body") from one layer to
  another, per footprint.
- **Per-primitive**: click any shape in the render to select it (each primitive
  gets an SVG group id `p-{kind}-{index}`, nested in `layer-{id}` — see
  `PcbComponentRenderer.RenderGroupedByLayer`). Shift+click adds to selection; Tab
  flood-fills to everything else on the same layer whose bounding box touches the
  current selection (good for grabbing a multi-segment outline drawn as separate
  tracks). Move the whole selection to any layer in one action.
- **Content-warning-driven**: a flagged layer/kind combo (e.g. "Top 3D Body has 4
  non-body items on it") auto-reveals that exact reassignment dropdown in the
  Layers panel, same as an illegal-layer flag, with an unrestricted (not
  legal-only) target list.
- All reassignment keeps Region/ComponentBody's *text* layer field
  (`V7LayerName`/`LayerName`) in sync with the numeric `.Layer` (the writer reads
  the text field, not the number, for these two types — format-notes §2), and
  patches Altium's "SmartUnion" stale-layer cache for linked-shape tools like
  Place Rectangle (format-notes §8, the single biggest gotcha in this whole
  project — confirmed fixed in real Altium).

### Generation (filling in *missing* documentation, not just fixing wrong layers)
Three independent, per-footprint actions in a "Generate Drawings" panel
(`Program.cs`: `GenerateCourtyard`, `GenerateAssembly`, `GeneratePin1Indicator`):

- **Courtyard**: unions the 3D body's bounding box and every pad's
  (rotation-aware) bounding box, each expanded 0.15mm, into a rectilinear
  ("boxy," axis-aligned-only) outline via `BoxyUnionOutline` — a coordinate-
  compression + grid + boundary-edge contour trace (standard algorithm, ~100
  lines, no arcs). When there's no 3D body, falls back to `SingleBoxOutline` — one
  continuous rectangle around all pads, not a per-pad shape-following union
  (deliberately simpler; disjointed outlines around a 2-pad part were explicitly
  rejected). Clears the target Courtyard layer first.
- **Assembly outline**: per body, prefers a true top-down projection of the
  embedded STEP model (`OriginalCircuit.Altium.Rendering.Step`'s
  `StepBodyOutline.TryProjectTopDown` — see §4, now implemented) — an accurate
  trace of the real 3D geometry, not the rough polygon Altium stores — falling
  back to the stored `Outline` for any body whose model can't be projected (no
  STEP data, unsupported STEP feature, or it times out — see §4), and further to
  `SingleBoxOutline` around all pads (no margin) when there's no body at all.
  Optional ".Designator" special string: centered via real Altium justification
  (see §3 below), height capped by the Y-extent, sized so ~4 average characters
  fit the X-extent (the 11-char ".Designator" placeholder itself is allowed to
  overflow — it's standing in for a short real designator like "R1"), hard 40 mil
  ceiling, 20:3 height:stroke-width ratio. Unchecking "Include .Designator"
  preserves any designator text already on the layer (doesn't delete it). The
  returned message names the actual source used (e.g. "Generated Top Assembly
  from STEP model projection: ...").
- **Pin-1 indicator**: separate action, doesn't clear the layer (additive only).
  Finds the pad literally designated "1", draws a ring **inside** the pad
  (radius = half the pad's smaller dimension minus a 0.1mm edge margin, floored so
  tiny pads still get something visible) — not a ring around/outside the pad,
  that was the first, wrong attempt.
- All three target Top or Bottom based on the same `MountSide` detection as the
  content warnings, defaulting to Top when ambiguous.
- **Courtyard** also benefits from the STEP projection: each body's rectangle in
  the boxy union comes from the projected silhouette's bounding box (tighter/more
  accurate than the stored `Outline`'s bounds) when available, falling back to
  `Bounds` otherwise. The courtyard union itself is still rects-only (unchanged,
  still "boxy" per the original spec) — only the per-body rectangle improved.

---

## 2. Key `.PcbLib` format findings (full detail in `ALTIUM-PCBLIB-FORMAT-NOTES.md`)

Quick-reference index — see the linked doc for offsets, byte tables, and the
methodology behind each:

1. **Classic layer scheme** (ids 1–82) — Top/Bottom/Overlay/Paste/Solder/Internal
   Plane/Mechanical 1-16/Drill/Multi-Layer, still correct for everything except
   mechanical >16.
2. **Mechanical layers beyond 16** — legacy single-byte layer field clamps to 72;
   the true number lives in a hidden second byte (offset varies per primitive
   type: Track=41, Text=226, Arc=52, Fill=42) or, for Region/ComponentBody, a text
   field (`"MECHANICAL23"`). Our internal id scheme: `N<=16 ? 56+N : 1000+N`.
3. **Layer names for the extended range** — `LAYER_V8_{Y}` table, where `Y` is
   *not* a stable position across files; the reliable key is each slot's own
   `LAYERID` field's low byte.
4. **Which layers are actually "enabled"** — Altium writes a full name template
   for every possible slot regardless of on/off state. Real enabled signal:
   Mid-Layer/Internal-Plane via PREV/NEXT chain membership; Mechanical via
   `LAYER_V8_{Y}MECHENABLED` being the literal string `"TRUE"` (not just key
   presence — this bit the original implementation).
5. **`PcbComponentBody.StandoffHeight` is signed**, not a magnitude — negative
   means the body dips below the board surface, the signature of a mid-mount/
   board-cutout part. Used for mount-side detection.
6. **`PcbText` justification** — `InvertedRectJustification` (byte offset 132) is
   the *general* text anchor, not exclusive to inverted-rectangle frames as the
   name suggests; `Location` is only the bottom-left corner for `LeftBottom`.
   **Critically**, it only takes effect when the separate `IsJustificationValid`
   flag (byte offset 240) is also `true` — every real Altium-authored text record
   has this set regardless of justification; our first attempt at fixing this
   missed the flag and the symptom (correct justification byte, still rendered
   bottom-left in real Altium) looked identical to the byte-mapping being wrong.
   Confirmed against two user-supplied ground-truth libraries, the second an
   8-component library covering the full 3×3 justification grid.
7. **The SmartUnion trap** — Altium's linked-shape tools (Place Rectangle) cache a
   *second, independent* copy of the group's layer in the footprint header
   (`SMARTUNION_ITEM{N}` inside `AdditionalParameters`), which must be patched on
   reassignment or the group visually stays on the old layer in real Altium
   despite every member primitive being correctly updated.

---

## 3. Git / fork / submodule infrastructure (set up this session)

**Context**: this project's two folders (`Layer Auditor` and the legacy
`AltiumSharp-master`) are both actually the *same* git repo —
`BevelTipDrip/Altium-Library-Layer-Auditor` on GitHub — with the AltiumSharp
library source copied directly into `Layer Auditor/src/` (no shared git history
with the original author's repo). The `shared/` folder holds true git
submodule-style dependencies.

**Goal**: be able to document/track our modifications against the real upstream
history, so genuinely reusable fixes (mechanical>16 handling, enabled-layer
gating, SmartUnion sync, text justification, the `AltiumStrokeFont` visibility
change, the `PcbComponent.Remove*` method additions, etc.) can eventually become
clean PRs back to the original author (`issus`).

**What was done**: forked all 5 relevant `issus` repos **publicly** into the
user's own account (`BevelTipDrip`):

| Repo | Role |
|---|---|
| `AltiumSharp` | The core library `Layer Auditor/src/` was copied from. Cloned as a **sibling folder** `AltiumSharp-fork/` next to `Layer Auditor/` — deliberately *not* part of Layer Auditor's own working tree, since it's a staging area for preparing clean upstream commits, not a build dependency. |
| `OriginalCircuit.Eda.Abstractions` | Already vendored in `Layer Auditor/shared/`; remote repointed. |
| `OriginalCircuit.Eda.Rendering` | Already vendored in `Layer Auditor/shared/`; remote repointed. |
| `OriginalCircuit.Mech.STEP` | **Newly vendored** into `Layer Auditor/shared/` — needed for the 3D projection work (see §4). |
| `OriginalCircuit.Mech.GLTF` | **Newly vendored** into `Layer Auditor/shared/` — same. |

**Remote convention** used consistently across all 5: `origin` = your fork
(`BevelTipDrip/...`), `upstream` = the original author (`issus/...`). Standard
fork workflow — `git fetch upstream` to pull the author's future updates, `git
push origin` for your own commits, eventually a PR from a branch in your fork
back to `issus`'s repo.

**`Layer Auditor/.gitmodules`** was created (didn't exist before — the two
already-vendored submodules were tracked as bare gitlink references in the git
index with no recorded URL, meaning a fresh clone had no way to know they existed
without manual instructions). Now registers all 4 `shared/` dependencies pointing
at the `BevelTipDrip` forks. Committed and pushed
(`e0b89e8`, "Vendor Mech.STEP and Mech.GLTF as submodules, register all shared
deps in .gitmodules").

**Not yet done**: no actual code changes have been ported into `AltiumSharp-fork`
yet (it's a clean clone of the fork, which is itself a clean copy of `issus`'s
current `main`). Preparing the actual upstream-worthy commits (isolating each fix
from Layer Auditor's `src/` copy into a clean commit on top of real AltiumSharp
history) is future work, not started.

---

## 4. Accurate 3D-body projection — implemented

**Motivation**: `PcbComponentBody.Outline` (what courtyard/assembly generation
used to rely on exclusively) is a rough 2D polygon Altium stores alongside the 3D
body — not a true top-down projection of the actual model. A real projection
makes generated courtyards/assembly outlines meaningfully more accurate. This
was researched in an earlier part of this session (verdict: an integration task,
not a from-scratch geometry project — findings below, still accurate) and has
now been built.

**What was built**: a new project, `Layer Auditor/src/OriginalCircuit.Altium.Rendering.Step/`
(`OriginalCircuit.Altium.Rendering.Step.csproj`, referencing `OriginalCircuit.Altium`
and the vendored `Mech.STEP.Tessellation` — deliberately **not** `Mech.GLTF`,
since no glTF export is needed, just the triangle mesh). Its one public type,
`StepBodyOutline`, exposes:

```csharp
List<List<CoordPoint>>? StepBodyOutline.TryProjectTopDown(
    PcbComponentBody body, IReadOnlyList<PcbModel> models, ProjectionCache? cache = null)
```

For a body with a resolvable STEP model, this: parses the STEP text
(`StepModel.Parse`), tessellates it (`Tessellator.TessellateScene()`, falling
back to tessellating each `Representation` directly if the model has no assembly
occurrences — same two-tier strategy as `GltfComponentPlacer.CollectMeshes`),
applies the body's placement transform (rotate by `Model3DRotX/Y/Z +
Model2DRotation`, translate by `Model2DLocation` — the exact same math as
`GltfComponentPlacer.EmitBody`, minus the Z/standoff/board-stack/bottom-mirroring
terms, which only affect height and are irrelevant once Z is dropped for a
top-down silhouette), then rasterizes the projected 2D triangles onto a uniform
grid (cell size ~ `extent/300`, clamped to 0.01–0.05mm) and traces the filled
cells' boundary into closed loops — the same "grid + boundary edge" contour
trace `Program.cs`'s `BoxyUnionOutline` already used for rectangles, generalized
to a point-in-triangle fill test. Returns `null` (caller falls back to the
stored `Outline`) when there's no `ModelId`, no matching `PcbModel`, empty
`StepData`, or the STEP payload can't be parsed/tessellated.

**A real silhouette staircases at the grid's resolution** (even a very slightly
tilted true rectangle rasterizes as a one-cell-per-row staircase — every step is
a genuine corner, so plain exact-collinear merging can't touch it). Fixed with a
closed-loop Douglas-Peucker simplification (`SimplifyLoop`/`DouglasPeucker` in
`StepBodyOutline.cs`; splits the loop at its farthest-apart point pair into two
open chains, simplifies each independently, rejoins) at a tolerance equal to the
grid's own cell size — collapsed a real QFN body's rasterized outline from ~100
points down to 6 while keeping the bounding box within ~0.015mm of the stored
`Outline`'s. This DP step is local to `StepBodyOutline.cs`;
`BoxyUnionOutline`/`SimplifyCollinear` in `Program.cs` (rectangle-only, always
exactly axis-aligned) were left untouched.

**Performance finding — large multi-part models can be pathologically slow to
tessellate**: a 900-ball BGA (`PCB - LEADLESS - BGA - AMD XILINX FCBGA-900
FFG900 31X31.PcbLib`) took **over 20 minutes** to tessellate via
`Tessellator.TessellateScene()` — confirmed via a temporary diagnostic endpoint
that `StepModel.Parse` itself is fast (1.26MB STEP text, 28,061 instances, 50ms)
so the cost is entirely inside tessellation, and that it is **not** curve-segment
(chord tolerance) driven — 0.5mm/0.2mm/0.1mm chord tolerances were all equally
slow, all still running past a 25s cap. Root cause not isolated further (likely
something in `Mech.STEP`'s per-occurrence assembly/tessellation-cache handling
scaling badly with hundreds of distinct-but-identical placed sub-parts, e.g. each
solder ball being its own assembly occurrence) — this would be a good finding to
raise with the `Mech.STEP` author, but wasn't chased into that vendored repo this
session. **Mitigation**: `StepBodyOutline.GetLocalTriangles` now runs
parse+tessellate on a background `Task` with an **8-second wall-clock budget**
(`TessellationBudget`); past it, the attempt is abandoned (the background work
keeps running to completion but its result is discarded — there's no
`CancellationToken` support in `Tessellator` to abort it cleanly) and the caller
falls back to the stored `Outline`, exactly like any other unprojectable model.
Confirmed: the FCBGA-900 case now returns in ~8s instead of hanging, generation
completes, courtyard/assembly fall back to the pre-existing (rough) behavior for
that one footprint.

**Wired into `Program.cs`**:
- `GenerateAssembly`: per body, prefers `StepBodyOutline.TryProjectTopDown`
  (traced exactly, no boxy simplification — same "look like the part" intent as
  the original stored-`Outline` behavior), falling back to `body.Outline` for any
  body that couldn't be projected. The returned message names the actual source
  (`"STEP model projection"`, a mixed `"STEP model projection (N/M bodies) +
  stored 3D body outline"`, or `"3D body outline"`).
- `GenerateCourtyard`: per body, the rectangle fed into the existing
  `BoxyUnionOutline` union comes from the *projected* silhouette's bounding box
  when available (tighter than the stored `Outline`'s bounds), else `b.Bounds`
  — the courtyard union itself is unchanged (still rects-only, still "boxy" per
  the original spec); only the per-body rectangle got more accurate.
- Both share one `StepBodyOutline.ProjectionCache` per generation call (via
  `StepBodyOutline.CreateCache()`) so a footprint with multiple bodies sharing a
  model tessellates it once.

**Verified against**: the repo's `BODY_3D_STEP.PcbLib` fixture, three real
distributor libraries (`PSEMI QFN-24 4x4`, `AD LFCSP-24 4X4MM`, `QORVO
RFSW6024` — all projected to within ~0.015mm of their datasheet body dimensions,
6/6/4 points after DP simplification), and two footprints from the user's
ground-truth library (`PCB-edited(3)-edited.PcbLib`: `PW0014A_M`, an SOP-14, and
`CUI-SJ-3506-SMT-TR_V`, the known mid-mount part) via the real
`/api/generate/assembly` and `/api/generate/courtyard` endpoints — both reported
`"STEP model projection"` as the source. Full round-trip verified: generate →
`/api/export` → re-upload through the app's own reader → same footprint
count/names/pad counts, and the re-imported footprint's `contentWarnings` came
back empty (previously flagged for a missing/empty Assembly/Courtyard).

**Findings from the earlier research pass** (still accurate background context):

- **The real 3D data is already available.** `PcbModel.StepData`
  (`Layer Auditor/src/OriginalCircuit.Altium/Models/Pcb/PcbModel.cs`) holds the
  embedded model as raw **STEP text** (ISO-10303-21 format), zlib-decompressed
  from the library's `Library/Models` OLE storage. `PcbLibReader` already extracts
  this today. Confirmed: Altium embeds `.step` files exclusively for component
  bodies — no GLTF/glb embedding path exists in `.PcbLib`.
- **`PcbRealisticRenderer`** (the "fab-house realistic" renderer, hoped to already
  do this) **does not render component bodies at all** — it only handles board
  outline, solder mask, substrate, and copper. No wiring to `PcbModel`/
  `ComponentBody` exists there. Only `PcbComponentRenderer.RenderComponentBody`
  draws anything for a body, and it just walks the stored (rough) `Outline`.
- **A complete, working pipeline for real STEP → projection already exists — in
  `AltiumSharp-master`, not (yet) in `Layer Auditor`**:
  - `Mech.STEP` (now forked, see §3) is a full ISO-10303-21 parser + B-rep
    geometry kernel + tessellator (`Mech.STEP.Tessellation.Tessellator` — B-rep to
    triangle mesh) + a renderer layer (`Mech.STEP.Render.Renderer.Bounds()` for a
    real 3D AABB, and **`Mech.STEP.Render.OutlineRenderer`** — a purpose-built
    top-down/hidden-line-removal 2D projection renderer, default view direction
    straight down, its own docs say "designed for 2D documentation of PCBs and
    their components").
  - `Mech.GLTF` (now forked) is a spec-conformant glTF reader/writer plus a bridge
    (`Mech.GLTF.Step`) between the STEP tessellation contract and glTF.
  - `AltiumSharp-master/src/OriginalCircuit.Altium.Rendering.Gltf/`
    (**does not exist in `Layer Auditor/src/` — not copied over originally**)
    contains `GltfComponentPlacer.cs`, which already: parses `PcbModel.StepData`
    via `StepModel.Parse`, tessellates it, and applies the **full Altium
    placement transform chain** — model's own rotation, `ComponentBody`'s
    `Model3DRotX/Y/Z`, footprint `Model2DRotation`/`Model2DLocation`, standoff
    height, and bottom-side mirroring — to produce true board-space 3D mesh
    positions for every placed body. This is exactly the hard, Altium-specific
    part (correct placement math), and it's already solved.
- **Verdict** (confirmed correct by the implementation above): this was an
  integration task, not a from-scratch geometry project. STEP parsing and
  tessellation came straight from `Mech.STEP`; the placement math was ported
  from `GltfComponentPlacer.EmitBody`'s formulas (not the file itself — see
  below). The one genuinely new piece was the top-down silhouette *extraction*
  itself (turning a triangle soup into a clean outline polygon).

**Deviations from the original plan** (worth knowing if picking this up again):
- **`OutlineRenderer` (`Mech.STEP.Render`) was not used.** It's a full
  hidden-line-removal line-drawing renderer (raster depth buffer, camera
  fitting, feature-edge classification) built for rendering a part's visible
  *edges* as a technical drawing — more machinery than a top-down silhouette
  needs, and it emits disconnected line segments rather than closed polygons
  (would still need contour assembly on top). Since a straight-down "shadow" of
  every triangle needs no hidden-surface removal at all (occluded triangles'
  projections are already fully contained within the silhouette), `StepBodyOutline`
  instead rasterizes projected triangles directly onto a grid and traces the
  boundary — reusing the same technique `BoxyUnionOutline` already used for
  rectangles, just generalized to a triangle fill test. Simpler, and it hands
  back exactly the closed-loop `List<List<CoordPoint>>` shape the rest of the
  generation code already expects.
- **`GltfComponentPlacer.cs` itself was not copied in** (no
  `OriginalCircuit.Altium.Rendering.Gltf` project exists in `Layer Auditor/src/`
  and none was added) — only its placement *formulas* were reproduced in
  `StepBodyOutline.PlaceXY`/`GetLocalTriangles`, minus every term that only
  affects Z (standoff, board Z-stack, bottom-side mirroring), since a top-down
  projection drops Z entirely. `Mech.GLTF` is still vendored (§3) but this
  feature doesn't reference it — no glTF export is involved.
- **A hard tessellation time budget was needed** — not anticipated in the
  original plan, which only flagged performance as "untested." See the
  FCBGA-900 finding above.

---

## 5. Open items / not yet done

- `AltiumSharp-fork` has no cherry-picked fixes yet — still an exact copy of
  `issus/AltiumSharp`'s current `main`. The STEP-projection work in §4 is one
  candidate; `Mech.STEP`'s tessellation slowness on many-occurrence models (also
  §4) is a candidate finding to raise with that repo's author, not something to
  fix from this side.
- `PcbRealisticRenderer.cs:643` has a known, separate, still-dead justification
  bug (`text.Justification`, a different unpopulated property) — not fixed since
  that renderer isn't used by this app; noted in format-notes §7 as a related gap.
- No automated tests were added for any of this session's features (courtyard/
  assembly/pin-1 generation, multi-select, content warnings, STEP projection) —
  all verification was manual: direct API calls against real user-supplied and
  distributor libraries, plus round-trip export/re-import checks for the
  justification fix and the STEP-projected assembly outline.
- STEP-projection root cause for the 900-occurrence slowdown (§4) wasn't
  isolated beyond "it's inside `Tessellator.TessellateScene()`, not parsing, and
  not chord-tolerance-sensitive" — a deeper look (e.g. whether `AssemblyModel.Build`
  or per-occurrence mesh caching scales badly with occurrence count) is future
  work if larger BGA-class parts need real (non-fallback) projected outlines too.
