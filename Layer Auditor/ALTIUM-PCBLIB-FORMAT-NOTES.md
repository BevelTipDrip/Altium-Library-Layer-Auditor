# Altium `.PcbLib` Format Findings

Notes from reverse-engineering the parts of Altium's binary PCB library format that
AltiumSharp didn't already handle correctly — specifically, everything to do with
PCB layer numbering beyond the "classic" 82-layer scheme. All findings here were
confirmed empirically (raw byte inspection, cross-checked against Altium's own UI
live, and validated with test libraries built specifically to exercise edge cases)
before being implemented, and re-verified after implementation via round-trip tests
(write → read back with our own code) and by opening exported files in real Altium.

This file is a reference for *why* the code looks the way it does in a few
non-obvious places. It complements the inline doc comments at each site — read this
first for the overall picture, then the doc comments for the exact mechanics.

## 1. The classic layer numbering scheme (ids 1–82)

Altium's internal layer id scheme, used throughout AltiumSharp (`LayerColors`,
`PcbLayerGroups`, primitive `.Layer` properties):

| ids | meaning |
|---|---|
| 1 | Top Layer |
| 2–31 | Mid-Layer 1–30 (internal copper) |
| 32 | Bottom Layer |
| 33 / 34 | Top / Bottom Overlay (silkscreen) |
| 35 / 36 | Top / Bottom Paste |
| 37 / 38 | Top / Bottom Solder (mask) |
| 39–54 | Internal Plane 1–16 |
| 55 | Drill Guide |
| 56 | Keep-Out Layer |
| 57–72 | **Mechanical 1–16** |
| 73 | Drill Drawing |
| 74 | Multi-Layer |
| 75–80 | Connections, Background, DRC Error Markers, Selections, Visible Grid 1/2 |
| 81 / 82 | Pad Holes / Via Holes |

This scheme dates back to when Altium only supported 16 mechanical layers, and it's
baked into the binary file format at a low level (see §2). It's still correct and
in active use for everything except mechanical layers beyond 16.

## 2. Mechanical layers beyond 16 — the core problem

Altium now supports far more than 16 mechanical layers (confirmed live, via a
user-built test library, up to at least **Mechanical 89** — there is no known fixed
cap). But every primitive's binary record only has a **single legacy byte** at
offset 0 for its layer, inherited from the original 16-layer design. A primitive on
Mechanical 17+ still writes that legacy byte **clamped to 72** (Mechanical 16) for
backward compatibility with older readers — the true layer has to come from
somewhere else.

Where "somewhere else" is depends on the primitive type:

### Region and ComponentBody: a text field

These two primitive kinds carry a `V7_LAYER` (Region) / implicit `LayerName`
(ComponentBody) string parameter alongside the legacy byte, e.g. `"MECHANICAL23"`.
This was already partially wired up before this investigation; we extended it to
handle numbers past 16 (previously the parser only accepted 1–16 and silently
dropped anything higher).

- Read: `PcbLibReader.ResolveExtendedMechanicalLayer` (`src/OriginalCircuit.Altium/Serialization/Readers/PcbLibReader.cs:947`)
- Write (legacy byte from the text): `PcbLibWriter.LayerNameToByte`
- Write (canonical text from a numeric layer): `PcbDocWriter.LayerByteToName` (`src/OriginalCircuit.Altium/Serialization/Writers/PcbDocWriter.cs:1142`, made `public` since callers reassigning a Region/ComponentBody's layer must also update this text field — see §5)

### Everything else (Track, Arc, Fill, Text, ...): a hidden second byte

These primitive kinds have **no text field at all** — pure fixed-offset binary
records. We found (by comparing a user-built test library against Altium's live UI,
using a CSV export of ground-truth layer assignments) that a *second* byte
elsewhere in each record — previously treated as reserved padding — reliably holds
the absolute Mechanical N number whenever the legacy byte says "mechanical"
(57–72), with **no upper bound**. It agrees with the legacy byte in the un-clamped
1–16 case too (legacy=57 ⇒ this byte=1), which is what let us find it: diffing
known-good vs. clamped primitives showed exactly one byte that tracked the true
layer consistently.

The offset is different per primitive type (different binary layout each):

| Primitive | Offset | Confirmed |
|---|---|---|
| Track | 41 | ✅ live Altium cross-check |
| Text | 226 | ✅ live Altium cross-check |
| Arc | 52 | ✅ round-trip self-test |
| Fill | 42 | ✅ round-trip self-test |
| Pad | — | ❌ not implemented — pads don't meaningfully go on mechanical layers, and Pad uses a different streaming read (not a flat byte array), so it wasn't worth retrofitting |
| Via | — | ❌ same reasoning as Pad |

Read: `PcbLibReader.ResolveMechanicalLayerByte` (`PcbLibReader.cs:971`), called from
each type's reader at the offsets above (`PcbLibReader.cs:1008`, `:1415`, `:1622`,
plus Text at `:1464`).

Write: the writer already had **half of this figured out before we started** — a
`V7LayerId(int layer)` helper (`PcbLibWriter.cs:639`) that computes a "v7 saved
layer id" `uint32` and writes it at the *same* offsets (the low byte of that
`uint32`, written little-endian, is exactly the hidden byte above). Its own doc
comment credits `altium_monkey`'s `PcbV7LayerPartition` as prior art for the
encoding scheme — worth looking up if extending this further. We extended it to
handle N>16 (previously fell through to a wrong "Multi-Layer" default), and fixed
the legacy-byte writer (`PcbLibWriter.LegacyLayerByte`, `PcbLibWriter.cs:670`) which
was blindly casting to `byte` — for N>16 that silently wrapped (e.g. layer 1020 →
byte 252) instead of correctly clamping to 72.

### `V7LayerId` encoding (confirmed, not just inferred)

```
0x0100_0000 + layer        signal layers 1–32 (0x0100_FFFF sentinel for layer 32/Bottom)
0x0101_0000 + (layer-38)   internal plane 39–54
0x0102_0000 + (layer-56)   mechanical 1–16
0x0102_0000 + N            mechanical 17+ (extended — same base, no cap)
0x0103_0000 + partition    special layers (overlay, paste, solder, drill, multi-layer, ...)
```

We independently rediscovered the `0x0102_0000 + N` mechanical formula from raw
bytes before finding the writer already partially implemented it — strong
cross-validation. This is also the *scripting-API-adjacent* encoding: a user
reading Altium's live scripting `TLayer` value for a mechanical layer got
`0x0400_0000 + N` (a different base, `0x4000000`, likely Altium's actual
`TLayer`/`LayerUtils` scripting constant) — same low-bits-encode-N structure, two
different numbering systems (one is this file's own internal cache id, the other is
what Altium's scripting API exposes). If a DelphiScript-based tool is ever built
against this library, use the `0x0400_0000` base, not `0x0102_0000`.

### Our internal id scheme

AltiumSharp's own numeric ids for mechanical layers past 16 needed a new,
collision-free range (ids 1–82 are fully allocated). We chose:

```
MechanicalLayerId(N) = N <= 16 ? 56 + N : 1000 + N
```

`PcbLibReader.MechanicalLayerId` (`PcbLibReader.cs:937`) is the single source of
truth for this; `PcbLayerGroups.IsMechanical`, `LayerColors.GetName` /
`GetColor` / `GetDrawPriority`, and the app's `DisplayLayerNumber` /
`FormatLayerName` (`PcbLibViewer/Program.cs`) all key off the same `>= 1017` check.
1–16 deliberately keeps the original 57–72 numbering unchanged.

## 3. Layer *names* for the extended range

Getting the numeric layer right is one thing; getting a human-meaningful name
(e.g. "Bottom Assembly" instead of generic "Mechanical 20") is a separate problem,
solved with a separate piece of file data.

### Classic table: `LAYER{N}NAME` / `V7_LAYER{N}NAME`

Covers ids 1–82 only (`PcbLayerStack.FromBoardParameters`,
`src/OriginalCircuit.Altium/Models/Pcb/PcbLayerStack.cs`). No room for N>16
mechanical — this table simply has no slots past `LAYER72NAME`.

### `LAYER_V8_{Y}` table: names for the extended range, but tricky to use correctly

This is a *flat sequential* table (`Y` = 0, 1, 2, ...) that also covers custom
names for Mechanical 17+. The catch: **`Y` is not a fixed schema position** — it's
relative to each file's own layer configuration and does **not** generalize across
files. We confirmed this the hard way: an initial implementation hardcoded `Y=40`
↦ Mechanical 17 (fitted to one test file) and got **wrong names** on a second,
more complex test file (`Y=40` meant Mechanical 14 there instead).

The robust fix: each mechanical `LAYER_V8_{Y}` slot also carries its own
`LAYER_V8_{Y}LAYERID` field (a large integer, the *file-internal* cache id from
§2's `0x0102_0000 + N` scheme) whose **low byte reliably equals the true
mechanical number**, independent of `Y`. `LAYER_V8_{Y}MECHENABLED` presence marks a
slot as mechanical (vs. a copper/overlay/etc. slot, whose `LAYERID` low byte means
nothing for our purposes). This was confirmed against two independent files.

Implementation: `PcbLayerStack.FromBoardParameters` (`PcbLayerStack.cs:101` region),
only fills gaps the classic table left — never overrides it — since the classic
table is still authoritative for 1–82.

If a file has no `LAYER_V8_*` data for a given Mechanical N>16 (no custom name was
ever set), it falls back to the generic `"Mechanical N"` from `LayerColors.GetName`
— correct, just not pretty.

## 4. Signal-layer naming confusion (not a bug — noted for future reference)

While chasing the above, we hit an apparent mismatch in signal/copper layer names
("Layer 1", "Layer 3", "Layer 5"... not matching physical stack order). This turned
out to be **expected Altium behavior, not a file-format issue**: Altium names
mid-layers in *creation order*, but its UI displays them in *physical stack
position* — the two numbers ([N] stack position vs. the "Layer N" label) are
independent by design. Nothing to fix here; flagging so it isn't re-investigated.

## 5. The "SmartUnion" trap — reassignment's biggest gotcha

This one cost the most debugging time and is the easiest to reintroduce if this
code is touched again without reading this section.

**Symptom**: reassigning a layer worked perfectly for every primitive type we
tested (Track, Arc, Fill, Region, ComponentBody, Text) — confirmed via round-trip
tests and raw byte diffs. But a specific object — 4 tracks drawn with Altium's
**"Place Rectangle" tool**, which links the 4 sides so they scale together as a
group — kept showing on its *old* layer when the exported file was reopened in
**real Altium**, even though our own reader correctly showed it on the new layer
for the same file. Byte-diffing the 4 individual track records showed them
byte-identical to a correct, successful reassignment — the bug wasn't in the
tracks at all.

**Root cause**: Altium's linked-shape tools (at least "Place Rectangle") cache a
**second, independent copy of the group's layer** in a `SMARTUNION_ITEM{N}` entry
at the **footprint header level** — not on any individual primitive. This is part
of `PcbComponent.AdditionalParameters`, a generic "preserve unknown keys verbatim"
catch-all (`PcbLibReader.ApplyComponentParameters`,
`PcbLibReader.cs:787`) that exists purely for round-trip fidelity of parameters
this library doesn't model — which is exactly why it survived untouched through a
correct reassignment of every primitive. Altium's editor apparently trusts this
cached copy over the member primitives' own layers when re-grouping them for
display.

The value is a **nested, escaped sub-record** — since the outer parameter block
already uses `=` and `|` as delimiters, the embedded one is escaped as `<EQ>` and
`<Pipe>`:

```
SMARTUNION_ITEM0=<Pipe>SELECTION<EQ>FALSE<Pipe>LAYER<EQ>MECHANICAL1<Pipe>LOCKED<EQ>FALSE<Pipe>...
```

**Fix**: `Program.cs`'s `SyncSmartUnions` (called from `ReassignLayer`) patches the
embedded `LAYER<EQ>{oldName}<Pipe>` token to `LAYER<EQ>{newName}<Pipe>` directly in
the raw string, using `PcbDocWriter.LayerByteToName` for the canonical name on both
sides, whenever a reassignment moves primitives off the layer that token names.

**Open question**: we only found and fixed this for "Place Rectangle" specifically.
If Altium has other linked/grouped primitive tools (rooms, polygons, etc.) that use
the same `SMARTUNION_ITEM*` mechanism or a different one entirely, they haven't
been tested. If a future reassignment silently fails to show up in Altium despite
looking correct in our own reader, **check `AdditionalParameters` for any embedded
layer reference first** — this is the pattern to look for.

## 6. Testing methodology that worked well

For anyone extending this further:

1. **Get ground truth from the user, not just from the file.** The breakthrough on
   the >16 mechanical range came from a user-built test library with a CSV export
   (via a small DelphiScript run in real Altium) of exact coordinates per layer —
   letting us join our raw byte dumps against confirmed layer assignments by
   position, rather than guessing at patterns. When live Altium access is
   available, use it for cross-checks before trusting an inferred pattern.
2. **Self-contained round-trip tests catch what single-file inspection can't.**
   Building a synthetic component in-memory, writing it, and reading it back (all
   within one throwaway console app) is what caught the Arc/Fill read-side gap —
   the writer already emitted the right data, the reader just never looked for it.
3. **Byte-diff before and after a change**, don't just check the fields you
   intended to change. The SmartUnion bug would have been invisible to a diff
   scoped to "the 4 track records" — it only showed up by grepping the *entire*
   raw file for every occurrence of the old layer's canonical name.
4. Scratch/throwaway console probes (referencing the built assemblies directly)
   are far faster for this kind of investigation than adding temporary
   `Console.Error.WriteLine` debug instrumentation to the library itself — but
   when raw byte offsets are genuinely unknown, temporary instrumentation in the
   reader (behind an env var, removed once the offset is confirmed) is the fastest
   way to dump full hex records for comparison.
