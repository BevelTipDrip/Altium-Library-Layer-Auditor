# OriginalCircuit.Altium.Rendering.Step

Projects an Altium `PcbComponentBody`'s embedded STEP model straight down onto the board plane and
traces its true silhouette, as an alternative to the rough 2D `Outline` polygon Altium stores
alongside the body. Used to generate accurate courtyard/assembly documentation for footprints that
have a 3D model but no (or a wrong) courtyard/assembly outline.

The STEP parser and B-rep tessellator are provided by `OriginalCircuit.Mech.STEP`; this package
contains the Altium-domain placement math (the body's 2D/3D rotation and location) and the
top-down rasterize-and-trace silhouette extraction. No native dependencies, no glTF involved.

## Usage

```csharp
using OriginalCircuit.Altium.Rendering.Step;

// body: PcbComponentBody with a ModelId; models: the library's PcbModel list (PcbLibrary.Models)
List<List<CoordPoint>>? outline = StepBodyOutline.TryProjectTopDown(body, models);
if (outline is not null)
{
    // one or more closed loops, in the footprint's own Coord space — draw them the same way
    // as the stored PcbComponentBody.Outline.
}
```

Returns `null` when the body has no usable STEP model (missing/empty `StepData`, an unresolvable
`ModelId`, or a payload that could not be parsed/tessellated) — callers should fall back to the
stored `Outline` in that case.
