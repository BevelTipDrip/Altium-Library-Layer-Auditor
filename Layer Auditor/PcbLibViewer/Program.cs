// ============================================================================
// Example: Altium PCB Library Viewer — an interactive web app
// ============================================================================
//
// A small single-page web app that lets you drag-and-drop an Altium PCB footprint
// library (.PcbLib) and browse every footprint inside it. For the selected
// footprint you get an editor-style render (not the photorealistic fab look —
// see BoardViewer for that) with a checkbox per PCB layer used by that footprint,
// so you can inspect exactly what got placed on which layer.
//
//   dotnet run --project examples/PcbLibViewer
//   → open the printed URL (e.g. http://localhost:5000) and drop in a .PcbLib.
//
// HOW LAYER TOGGLING WORKS
// ─────────────────────────
// PcbComponentRenderer.RenderGroupedByLayer wraps each PCB layer's primitives in a
// named SVG group (<g id="layer-33">...). The front-end toggles a layer purely by
// setting display:none on that group — no server round-trip, same trick BoardViewer
// uses for its physical (substrate/copper/soldermask/...) groups.
// ============================================================================

using System.Collections.Concurrent;
using System.Text;
using OriginalCircuit.Altium;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Altium.Rendering;
using OriginalCircuit.Altium.Rendering.Svg;
using OriginalCircuit.Altium.Serialization.Writers;
using OriginalCircuit.Eda.Primitives;
using OriginalCircuit.Eda.Rendering;

var builder = WebApplication.CreateBuilder(args);

// Libraries can be several MB; allow a generous upload size.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024 * 1024);
builder.Services.AddSingleton<LibraryCache>();

var app = builder.Build();
app.UseDefaultFiles();   // serve wwwroot/index.html at "/"
app.UseStaticFiles();

var svg = new SvgRenderer();

// ── Upload: parse the library once and cache it, return the footprint list ───
app.MapPost("/api/upload", async (IFormFile file, LibraryCache cache, CancellationToken ct) =>
{
    if (file.Length == 0) return Results.BadRequest(new { error = "Empty file." });

    PcbLibrary library;
    List<PcbComponent> components;
    Dictionary<int, string> layerNames;
    try
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var opened = await AltiumLibrary.OpenPcbLibAsync(buffer, ct);
        library = (PcbLibrary)opened;
        components = library.AllComponents.Cast<PcbComponent>().ToList();

        // Layer numbering is fixed (1=Top, 32=Bottom, 57-72=Mechanical, ...), but the DISPLAY name for
        // a given number is per-file: Altium lets a user rename e.g. layer 69 to "Top Assembly" or "Top
        // Courtyard", and that only lives in this library's own layer-stack header — so it must be read
        // fresh from each upload, never assumed from a fixed table.
        layerNames = library.LayerStack?.Layers
            .ToDictionary(l => l.Index, l => l.Name) ?? new Dictionary<int, string>();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Could not read '{file.FileName}': {ex.Message}" });
    }

    var id = cache.Add(library, components, layerNames, Path.GetFileNameWithoutExtension(file.FileName));
    var footprints = components.Select((c, i) => new
    {
        index = i,
        name = string.IsNullOrWhiteSpace(c.Name) ? $"Footprint {i + 1}" : c.Name,
        description = c.Description,
        widthMm = Math.Round(c.Bounds.Width.ToMm(), 2),
        heightMm = Math.Round(c.Bounds.Height.ToMm(), 2),
        pads = c.Pads.Count,
    });

    // Every layer used anywhere in the library, PLUS every layer the library's own layer-stack header
    // defines but that happens to have zero primitives on it anywhere yet (layerNames' keys) — the
    // master list the front-end builds its "Legal Layers" checklist and reassignment target pickers
    // from. Without the second half, a layer someone set up on purpose (e.g. a "Top Courtyard" that no
    // footprint has used yet) would be invisible as a reassignment target until something already used
    // it, defeating the point of fixing "compounded" primitives onto their own, currently-empty layer.
    // Mechanical layers (57-72) default to unchecked/illegal since that's exactly where Altium's
    // layer-Type-vs-number mismatches happen between libraries; the fixed-purpose layers (copper, silk,
    // paste, solder, ...) default to legal.
    var usedLayerIds = new HashSet<int>(components.SelectMany(AuditEntries).Select(e => e.Layer));
    var libraryLayers = usedLayerIds
        .Concat(layerNames.Keys)
        .Where(l => l is not (>= 75 and <= 80)) // Connections/Background/DRC Markers/Selections/Grid 1-2:
                                                  // Altium editor pseudo-layers, never a real primitive's home
        .Distinct()
        .OrderBy(l => l)
        .Select(l => new
        {
            id = l,
            name = FormatLayerName(l, layerNames.TryGetValue(l, out var n) ? n : LayerColors.GetName(l)),
            color = ToHex(LayerColors.GetColor(l)),
            mechanical = PcbLayerGroups.IsMechanical(l),
            used = usedLayerIds.Contains(l),
        });

    return Results.Json(new
    {
        id,
        name = Path.GetFileNameWithoutExtension(file.FileName),
        count = components.Count,
        footprints,
        libraryLayers,
    });
}).DisableAntiforgery();

// ── Render one footprint to SVG, grouped by layer ─────────────────────────────
app.MapPost("/api/render.svg", async (RenderRequest req, LibraryCache cache, CancellationToken ct) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (_, components, layerNames, _) = entry.Value;
    if (req.Index < 0 || req.Index >= components.Count)
        return Results.BadRequest(new { error = "Footprint index out of range." });

    var component = components[req.Index];
    var options = new RenderOptions
    {
        Width = Math.Clamp(req.Width ?? 900, 200, 4000),
        Height = Math.Clamp(req.Height ?? 700, 200, 4000),
        BackgroundColor = EdaColor.FromRgb(0x14, 0x18, 0x1f),
    };

    using var ms = new MemoryStream();
    var layerIds = await svg.RenderGroupedByLayerAsync(component, ms, options, cancellationToken: ct);
    var svgText = Encoding.UTF8.GetString(ms.ToArray());

    // Which kinds (Primitive / 3D Body) this footprint actually has on each layer — lets the
    // reassignment UI show one dropdown for a layer with only one kind, or two when a 3D body and a
    // flat primitive share the same (illegal) layer, exactly the case that needs different fixes.
    var kindsByLayer = AuditEntries(component)
        .GroupBy(e => e.Layer)
        .ToDictionary(g => g.Key, g => g.Select(e => e.Kind).Distinct().ToList());

    var layers = layerIds.Select(id => new
    {
        id,
        name = FormatLayerName(id, layerNames.TryGetValue(id, out var customName) ? customName : LayerColors.GetName(id)),
        color = ToHex(LayerColors.GetColor(id)),
        kinds = kindsByLayer.TryGetValue(id, out var k) ? k : new List<string>(),
    });

    // Content-sanity warnings for THIS footprint — computed live on every render (not gated behind the
    // library-wide "Generate Report" button) so the Layers panel can reveal a reassignment dropdown for
    // a flagged layer/kind the same way an illegal-layer flag does, as soon as the footprint is opened.
    var contentWarnings = ContentWarnings(component, layerNames);

    return Results.Json(new { svg = svgText, layers, contentWarnings });
});

// ── Audit report: components with primitives/3D bodies on layers not in the "legal" set, PLUS
// content warnings — footprints that use only legal layers but still have something wrong that the
// legal/illegal check can never see (e.g. a stray track on the Top 3D Body layer, or no 3D body at
// all). The two are reported separately (red "issues" vs. yellow "warnings" client-side) since they're
// different kinds of problem: one is "this layer shouldn't be used," the other is "this specific,
// otherwise-legal layer doesn't have what it should."
app.MapPost("/api/report", (ReportRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (_, components, layerNames, _) = entry.Value;
    var legal = new HashSet<int>(req.LegalLayers ?? new List<int>());

    string NameOf(int id) => FormatLayerName(id, layerNames.TryGetValue(id, out var n) ? n : LayerColors.GetName(id));

    var componentReports = new List<object>();
    var issueComponents = 0;
    var totalIssues = 0;
    var warningComponents = 0;
    var totalWarnings = 0;
    var body = new StringBuilder();
    var warningBody = new StringBuilder();

    foreach (var component in components)
    {
        var issues = AuditEntries(component)
            .Where(e => !legal.Contains(e.Layer))
            .GroupBy(e => (e.Layer, e.Kind))
            .Select(g => new { layerId = g.Key.Layer, layerName = NameOf(g.Key.Layer), kind = g.Key.Kind, count = g.Count() })
            .OrderBy(i => i.layerId).ThenBy(i => i.kind)
            .ToList();
        var warnings = ContentWarnings(component, layerNames);

        if (issues.Count == 0 && warnings.Count == 0) continue;

        var name = string.IsNullOrWhiteSpace(component.Name) ? "(unnamed)" : component.Name;
        componentReports.Add(new { name = component.Name, issues, warnings });

        if (issues.Count > 0)
        {
            issueComponents++;
            totalIssues += issues.Sum(i => i.count);
            body.AppendLine();
            body.AppendLine(name);
            foreach (var i in issues)
                body.AppendLine($"    {i.count,4}  {i.kind,-10} {i.layerName}");
        }

        if (warnings.Count > 0)
        {
            warningComponents++;
            totalWarnings += warnings.Count;
            warningBody.AppendLine();
            warningBody.AppendLine(name);
            foreach (var w in warnings)
                warningBody.AppendLine($"    - {w.Message}");
        }
    }

    var header = issueComponents == 0
        ? "No illegal layer usage found."
        : $"{issueComponents} component(s) with illegal layer usage — {totalIssues} issue(s) total.";
    var warningHeader = warningComponents == 0
        ? "No content warnings found."
        : $"{warningComponents} component(s) with content warnings — {totalWarnings} warning(s) total.";

    var text = $"Illegal Layer Usage Report\n{new string('=', 40)}\n{header}\n{body}\n" +
               $"Content Warnings\n{new string('=', 40)}\n{warningHeader}\n{warningBody}";

    return Results.Json(new
    {
        componentCount = issueComponents,
        totalIssues,
        warningComponentCount = warningComponents,
        totalWarnings,
        components = componentReports,
        text,
    });
});

// ── Reassign: move a footprint's flagged primitives/3D bodies from one layer to another ──────
// Mutates the cached in-memory model directly (no file write yet — see /api/export). Scoped to a
// single footprint per the chosen workflow: each footprint's fixes are independent, so the same
// mistake on a different footprint needs its own reassignment even if it's the same source/target.
app.MapPost("/api/reassign", (ReassignRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (_, components, _, _) = entry.Value;
    if (req.Index < 0 || req.Index >= components.Count)
        return Results.BadRequest(new { error = "Footprint index out of range." });

    var component = components[req.Index];
    var results = req.Reassignments.Select(rule => new
    {
        rule.FromLayer,
        rule.Kind,
        rule.ToLayer,
        moved = rule.FromLayer == rule.ToLayer ? 0 : ReassignLayer(component, rule.FromLayer, rule.Kind, rule.ToLayer),
    }).ToList();

    return Results.Json(new { moved = results.Sum(r => r.moved), results });
});

// ── Reassign one: move a single clicked primitive/3D body to a new layer ──────────────────────
// Companion to /api/reassign's by-source-layer sweep, for the case a bulk kind/layer rule can't
// express: two DIFFERENT primitives that happen to already share one (legal or illegal) layer — e.g.
// a "component center" mark and a courtyard outline both compounded onto the same layer — where only
// one of them should move. The primitive is identified by the same (kind, index) pair its SVG group id
// carries (see PcbComponentRenderer.RenderGroupedByLayer), which the front-end reads off the element
// the user actually clicked.
app.MapPost("/api/reassign-one", (ReassignOneRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (_, components, _, _) = entry.Value;
    if (req.Index < 0 || req.Index >= components.Count)
        return Results.BadRequest(new { error = "Footprint index out of range." });

    var component = components[req.Index];
    if (!ReassignOne(component, req.PrimKind, req.PrimIndex, req.ToLayer))
        return Results.BadRequest(new { error = "That primitive is gone — the footprint was likely modified since it was rendered. Re-render and try again." });

    return Results.Json(new { moved = true });
});

// ── Reassign many: move a multi-selection (shift+click, or Tab-expanded to touching primitives)
// of individually-picked primitives/3D bodies to one new layer, in a single round-trip. Each item is
// resolved independently through the same ReassignOne used by /api/reassign-one, so a selection that
// spans several source layers (e.g. shift-clicking things from two different layers) works the same
// as if each had been moved one at a time.
app.MapPost("/api/reassign-many", (ReassignManyRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (_, components, _, _) = entry.Value;
    if (req.Index < 0 || req.Index >= components.Count)
        return Results.BadRequest(new { error = "Footprint index out of range." });

    var component = components[req.Index];
    var moved = req.Prims.Count(p => ReassignOne(component, p.PrimKind, p.PrimIndex, req.ToLayer));

    return Results.Json(new { moved, total = req.Prims.Count });
});

// ── Export: serialize the current (possibly reassigned) in-memory library back to a .PcbLib ──────
app.MapPost("/api/export", async (ExportRequest req, LibraryCache cache, CancellationToken ct) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (library, _, _, name) = entry.Value;

    using var ms = new MemoryStream();
    await library.SaveAsync(ms, null, ct);
    return Results.File(ms.ToArray(), "application/octet-stream", $"{name}-edited.PcbLib");
});

app.Run();

// Moves every primitive/3D body of the given kind currently on fromLayer to toLayer, on one
// component. ComponentBody and Region are special: unlike every other primitive, the writer reads
// their *name* field (not the numeric layer) when serializing (see PcbLibWriter.WriteComponentBody),
// so LayerByteToName must be kept in sync alongside the numeric layer or the file would round-trip
// back to the OLD layer despite the in-memory model showing the new one.
static int ReassignLayer(PcbComponent c, int fromLayer, string kind, int toLayer)
{
    var count = 0;
    var layerName = PcbDocWriter.LayerByteToName(toLayer);

    if (kind == "3D Body")
    {
        foreach (var b in c.ComponentBodies.Cast<PcbComponentBody>())
        {
            if (b.Layer != fromLayer) continue;
            b.Layer = toLayer;
            b.LayerName = layerName;
            count++;
        }
        return count;
    }

    foreach (var t in c.Tracks.Cast<PcbTrack>()) if (t.Layer == fromLayer) { t.Layer = toLayer; count++; }
    foreach (var a in c.Arcs.Cast<PcbArc>()) if (a.Layer == fromLayer) { a.Layer = toLayer; count++; }
    foreach (var f in c.Fills.Cast<PcbFill>()) if (f.Layer == fromLayer) { f.Layer = toLayer; count++; }
    foreach (var r in c.Regions.Cast<PcbRegion>())
        if (r.Layer == fromLayer) { r.Layer = toLayer; r.V7LayerName = layerName; count++; }
    foreach (var tx in c.Texts.Cast<PcbText>()) if (tx.Layer == fromLayer) { tx.Layer = toLayer; count++; }
    foreach (var p in c.Pads.Cast<PcbPad>()) if (p.Layer == fromLayer) { p.Layer = toLayer; count++; }
    foreach (var v in c.Vias.Cast<PcbVia>()) if (v.Layer == fromLayer) { v.Layer = toLayer; count++; }
    SyncSmartUnions(c, fromLayer, toLayer);
    return count;
}

// Moves exactly one primitive/3D body — identified by the (kind, index) pair from its render-time
// "p-{kind}-{index}" SVG group id — to a new layer. kind/index must stay index-aligned with
// PcbComponentRenderer.Collect, since that's what the front-end's clicked element refers to. Returns
// false if the index is out of range (stale render).
static bool ReassignOne(PcbComponent c, string kind, int index, int toLayer)
{
    var layerName = PcbDocWriter.LayerByteToName(toLayer);
    int fromLayer;

    switch (kind)
    {
        case "Track":
            if (index < 0 || index >= c.Tracks.Count) return false;
            var track = (PcbTrack)c.Tracks[index];
            fromLayer = track.Layer; track.Layer = toLayer;
            break;
        case "Arc":
            if (index < 0 || index >= c.Arcs.Count) return false;
            var arc = (PcbArc)c.Arcs[index];
            fromLayer = arc.Layer; arc.Layer = toLayer;
            break;
        case "Fill":
            if (index < 0 || index >= c.Fills.Count) return false;
            var fill = (PcbFill)c.Fills[index];
            fromLayer = fill.Layer; fill.Layer = toLayer;
            break;
        case "Region":
            if (index < 0 || index >= c.Regions.Count) return false;
            var region = (PcbRegion)c.Regions[index];
            fromLayer = region.Layer; region.Layer = toLayer; region.V7LayerName = layerName;
            break;
        case "Text":
            if (index < 0 || index >= c.Texts.Count) return false;
            var text = (PcbText)c.Texts[index];
            fromLayer = text.Layer; text.Layer = toLayer;
            break;
        case "Pad":
            if (index < 0 || index >= c.Pads.Count) return false;
            var pad = (PcbPad)c.Pads[index];
            fromLayer = pad.Layer; pad.Layer = toLayer;
            break;
        case "Via":
            if (index < 0 || index >= c.Vias.Count) return false;
            var via = (PcbVia)c.Vias[index];
            fromLayer = via.Layer; via.Layer = toLayer;
            break;
        case "Body":
            if (index < 0 || index >= c.ComponentBodies.Count) return false;
            var body = (PcbComponentBody)c.ComponentBodies[index];
            body.Layer = toLayer; body.LayerName = layerName;
            return true; // 3D bodies are never SmartUnion members — nothing else to sync.
        default:
            return false;
    }

    SyncSmartUnions(c, fromLayer, toLayer);
    return true;
}

// Altium's "linked shape" tools (e.g. Place Rectangle, which draws 4 tracks whose corners stay
// joined so they scale together) cache a SEPARATE copy of the group's layer in a "SmartUnion" record
// at the footprint header level — not on any individual primitive. This reader has no typed model for
// it (it's an opaque per-file feature list, round-tripped verbatim through PcbComponent's
// AdditionalParameters catch-all — see PcbLibReader.ApplyComponentParameters), so reassigning the
// member primitives' own Layer fields leaves this cached copy stale: Altium trusts the SmartUnion
// record over the primitives when it re-groups them, so the group visually stays on the old layer
// even though every individual track correctly reports the new one. Patch the embedded
// "LAYER<EQ>{name}<Pipe>" token (Altium escapes '=' and '|' inside this nested sub-record since the
// outer parameter block already uses them as its own delimiters) directly in the raw string.
static void SyncSmartUnions(PcbComponent c, int fromLayer, int toLayer)
{
    if (c.AdditionalParameters is null) return;
    var fromTag = $"LAYER<EQ>{PcbDocWriter.LayerByteToName(fromLayer)}<Pipe>";
    var toTag = $"LAYER<EQ>{PcbDocWriter.LayerByteToName(toLayer)}<Pipe>";
    if (fromTag == toTag) return;

    foreach (var key in c.AdditionalParameters.Keys.Where(k => k.StartsWith("SMARTUNION_ITEM", StringComparison.OrdinalIgnoreCase)).ToList())
    {
        var value = c.AdditionalParameters[key];
        if (value.Contains(fromTag, StringComparison.Ordinal))
            c.AdditionalParameters[key] = value.Replace(fromTag, toTag, StringComparison.Ordinal);
    }
}

static string ToHex(uint argb) => $"#{(argb >> 16) & 0xFF:X2}{(argb >> 8) & 0xFF:X2}{argb & 0xFF:X2}";

// Standard layers (copper, silk, paste, solder, drill, multi-layer, ...) use the same small integer
// in this reader's internal IDs as Altium's own UI/scripting refer to them by, so showing the id
// verbatim is correct and familiar. Mechanical layers are different: this reader's internal IDs pack
// the original 16 mechanical slots into 57-72, and any higher mechanical number into 1000+N (both this
// file format's own offsets — see PcbLibReader.MechanicalLayerId), but Altium's UI and scripting API
// call them "Mechanical 1", "Mechanical 2", etc. with no fixed upper bound (confirmed live up to at
// least Mechanical 89) — the number a user actually recognizes and compares between libraries (e.g.
// "Courtyard on Mechanical 15 in one library vs. Mechanical 16 in another"). Convert back to that
// native index so the bracketed number means what a human auditing the library expects, not this
// reader's internal offset.
static int DisplayLayerNumber(int id) => id switch
{
    >= 57 and <= 72 => id - 56,   // Mechanical 1-16
    >= 1017 => id - 1000,          // Mechanical N>16 (extended range)
    _ => id,
};

// Layers with a single, fixed purpose — there's only ever one "Top Solder" — carry no number in
// Altium's own UI (unlike Mechanical N, where the number is what actually varies between libraries
// and is worth calling out). Top/Bottom Layer, Overlay, Paste, Solder, Drill Guide, Keep-Out,
// Drill Drawing, Multi-Layer, Pad/Via Holes.
static bool IsUnnumberedLayer(int id) => id is 1 or 32 or 33 or 34 or 35 or 36 or 37 or 38 or 55 or 56 or 73 or 74 or 81 or 82;

static string FormatLayerName(int id, string baseName) =>
    IsUnnumberedLayer(id) ? baseName : $"{baseName} ({DisplayLayerNumber(id)})";

// Every audited primitive on a component, as (layer, kind). A PcbComponentBody is Altium's actual
// "3D body" primitive type (see its doc comment); every other primitive kind is a flat 2D "Primitive"
// for this audit's purposes — matching the quantity/type/layer breakdown the report needs.
static IEnumerable<(int Layer, string Kind)> AuditEntries(PcbComponent c)
{
    foreach (var t in c.Tracks) yield return (t.Layer, "Primitive");
    foreach (var a in c.Arcs) yield return (a.Layer, "Primitive");
    foreach (var f in c.Fills) yield return (f.Layer, "Primitive");
    foreach (var r in c.Regions) yield return (r.Layer, "Primitive");
    foreach (var tx in c.Texts) yield return (tx.Layer, "Primitive");
    foreach (var p in c.Pads) yield return (p.Layer, "Primitive");
    foreach (var v in c.Vias.Cast<PcbVia>()) yield return (v.Layer, "Primitive");
    foreach (var b in c.ComponentBodies) yield return (b.Layer, "3D Body");
}

// Maps this library's own conventional layer names (case-insensitive, e.g. "Top 3D Body" — set via
// Altium's Mechanical Layer Editor "Layer Type" field) back to their numeric id, for the content
// checks below. Ids vary per library, but a footprint author who assigned a layer that Type expects
// it to be called exactly that in every library that follows the convention.
static Dictionary<string, int> LayersByName(Dictionary<int, string> layerNames)
{
    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var (id, name) in layerNames)
        if (!string.IsNullOrWhiteSpace(name) && !map.ContainsKey(name.Trim()))
            map[name.Trim()] = id;
    return map;
}

// Advisory content checks, independent of the legal/illegal layer audit — these catch a footprint
// that only uses legal layers but still has something wrong that "is this layer legal" can never see:
// a stray track on the 3D-body layer, a missing 3D body, a 3D body on the wrong layer, an empty
// courtyard/assembly outline. Keyed off the conventional names above rather than raw ids; a check is
// skipped entirely (not flagged) if the library never named the corresponding layer, since there's
// nothing to check against.
static List<ContentWarning> ContentWarnings(PcbComponent c, Dictionary<int, string> layerNames)
{
    var byName = LayersByName(layerNames);
    var warnings = new List<ContentWarning>();
    int? IdOf(string name) => byName.TryGetValue(name, out var id) ? id : null;

    var topBody = IdOf("Top 3D Body");
    var bottomBody = IdOf("Bottom 3D Body");
    var bodyLayers = new[] { topBody, bottomBody }.Where(id => id.HasValue).Select(id => id!.Value).ToList();

    if (bodyLayers.Count > 0)
    {
        // Anything other than a 3D body on the 3D-body layer itself. LayerId+Kind are set here (unlike
        // most other checks below) because this one is directly actionable: the front-end uses them to
        // reveal that exact layer's "Primitives" reassignment dropdown, same as an illegal-layer flag.
        foreach (var id in bodyLayers)
        {
            var nonBodyCount = AuditEntries(c).Count(e => e.Layer == id && e.Kind != "3D Body");
            if (nonBodyCount > 0)
                warnings.Add(new($"{layerNames[id]} has {nonBodyCount} non-3D-body item(s) on it.", id, "Primitive"));
        }

        // No 3D body anywhere on either the Top or Bottom 3D Body layer — this footprint has no 3D
        // model at all (a normal footprint puts its body on exactly one side, never both, so this only
        // fires when NEITHER has one). Not actionable via a reassignment dropdown — there's nothing to
        // move, the fix is adding a body — so no LayerId/Kind.
        var bodies = c.ComponentBodies.Cast<PcbComponentBody>().ToList();
        if (!bodies.Any(b => bodyLayers.Contains(b.Layer)))
            warnings.Add(new("No 3D body found on the Top or Bottom 3D Body layer.", null, null));

        // A 3D body that exists, but on some other layer entirely — actionable: the front-end reveals
        // that layer's "3D Bodies" dropdown so it can be moved to Top/Bottom 3D Body directly.
        foreach (var g in bodies.Where(b => !bodyLayers.Contains(b.Layer)).GroupBy(b => b.Layer))
        {
            var name = layerNames.TryGetValue(g.Key, out var n) ? n : LayerColors.GetName(g.Key);
            warnings.Add(new($"{g.Count()} 3D body item(s) found on '{name}' instead of Top/Bottom 3D Body.", g.Key, "3D Body"));
        }
    }

    // Courtyard and assembly are only expected on the side a part actually mounts on — a normal
    // top-only SMD part will never have a Bottom Courtyard, and flagging that as empty is just noise.
    // MountSide (below) guesses which side that is; a part it can't place a bet on (side is null) skips
    // both checks entirely rather than flag against a guess.
    bool Empty(string layerName)
    {
        var id = IdOf(layerName);
        return id is not null && !AuditEntries(c).Any(e => e.Layer == id);
    }
    bool Exists(string layerName) => IdOf(layerName) is not null;
    // Empty-layer warnings carry LayerId (so the front-end can still badge that specific row) but no
    // Kind — there's nothing sitting on the layer to reassign, the fix is adding content, not moving it.
    void FlagEmpty(string layerName)
    {
        if (Empty(layerName)) warnings.Add(new($"{layerName} is empty.", IdOf(layerName), null));
    }

    var side = MountSide(c, topBody, bottomBody);
    if (side == "top")
    {
        FlagEmpty("Top Courtyard");
    }
    else if (side == "bottom")
    {
        FlagEmpty("Bottom Courtyard");
    }
    else if (side == "both" && Exists("Top Courtyard") && Exists("Bottom Courtyard"))
    {
        // Through-hole / board-cutout exception: content could legitimately belong on either or both
        // sides, so only flag the extreme case — no courtyard drawn anywhere at all.
        if (Empty("Top Courtyard") && Empty("Bottom Courtyard"))
            warnings.Add(new("No courtyard found on either side (through-hole / board-cutout part).", null, null));
    }

    if (side == "bottom")
    {
        FlagEmpty("Bottom Assembly");
    }
    else if (side is "top" or "both")
    {
        FlagEmpty("Top Assembly");
    }

    return warnings;
}

// Best-effort guess at which side of the board a footprint mounts on, so the courtyard/assembly
// checks above only apply to the side that's actually expected to have content. Two real-world
// exceptions widen this to "both sides legitimately might have content, don't strictly require
// either": through-hole pads (a THT part's leads pass all the way through — connectors, mainly), and a
// 3D body whose StandoffHeight is negative, meaning the model geometry itself dips below the board
// surface (Z=0) — the signature of a board-cutout / mid-mount part. Confirmed against a real mid-mount
// USB connector in a user's library: StandoffHeight -2.74mm (spanning from below the board to just
// above it) vs. a normal top-only part's StandoffHeight of ~0 (sits entirely on/above the surface).
// Returns null — "unknown" — when there's no pad or body evidence to go on at all (e.g. a purely
// graphical/mechanical footprint), so the caller skips the check rather than guess.
static string? MountSide(PcbComponent c, int? topBody, int? bottomBody)
{
    var pads = c.Pads.Cast<PcbPad>().ToList();
    if (pads.Any(p => p.HoleSize > Coord.Zero)) return "both";
    if (c.ComponentBodies.Cast<PcbComponentBody>().Any(b => b.StandoffHeight < Coord.Zero)) return "both";

    var hasTopPad = pads.Any(p => p.Layer == 1);
    var hasBottomPad = pads.Any(p => p.Layer == 32);
    if (hasTopPad && !hasBottomPad) return "top";
    if (hasBottomPad && !hasTopPad) return "bottom";
    if (hasTopPad && hasBottomPad) return "both";

    // No SMD/THT pad evidence at all — fall back to whichever named 3D-body layer it's actually on.
    var bodies = c.ComponentBodies.Cast<PcbComponentBody>().ToList();
    var onTop = topBody is not null && bodies.Any(b => b.Layer == topBody);
    var onBottom = bottomBody is not null && bodies.Any(b => b.Layer == bottomBody);
    if (onTop && !onBottom) return "top";
    if (onBottom && !onTop) return "bottom";
    return null;
}

// One content-sanity finding from ContentWarnings. LayerId/Kind are set only when the warning is
// directly actionable via reassignment — the front-end uses them to reveal that specific layer's
// reassignment dropdown for that specific kind, exactly like an illegal-layer flag does — and left
// null when the fix isn't a reassignment (nothing missing has anywhere to reassign FROM).
record ContentWarning(string Message, int? LayerId, string? Kind);

// The render request sent by the front-end.
record RenderRequest(string Id, int Index, int? Width, int? Height);

// The audit-report request sent by the front-end: which layer IDs the user has marked legal.
record ReportRequest(string Id, List<int>? LegalLayers);

// One reassignment rule: move everything of Kind ("Primitive" or "3D Body") on FromLayer to ToLayer.
record ReassignRule(int FromLayer, string Kind, int ToLayer);

// The reassignment request: apply a batch of rules to one footprint.
record ReassignRequest(string Id, int Index, List<ReassignRule> Reassignments);

// The single-primitive reassignment request: PrimKind/PrimIndex identify the clicked primitive the
// same way its SVG group id does ("p-{PrimKind}-{PrimIndex}") — see PcbComponentRenderer.Collect.
record ReassignOneRequest(string Id, int Index, string PrimKind, int PrimIndex, int ToLayer);

// One primitive reference within a multi-select reassignment batch.
record PrimRef(string PrimKind, int PrimIndex);

// The multi-primitive reassignment request: every Prims entry moves to the same ToLayer.
record ReassignManyRequest(string Id, int Index, List<PrimRef> Prims, int ToLayer);

// The export request: just the library id — the whole (possibly reassigned) library is serialized.
record ExportRequest(string Id);

// A tiny bounded in-memory cache of parsed libraries, so switching footprints re-renders without
// re-uploading. LayerNames is that file's own layer-stack table (layer id -> display name), captured
// once at upload time since it's per-file, not a fixed global table. Library is the original parsed
// object — retained (not just the flat Components list) so /api/export can re-serialize it, since
// Components share the same object references Library holds internally, in-memory reassignments via
// ReassignLayer are already reflected there with no extra bookkeeping.
sealed class LibraryCache
{
    private const int Capacity = 8;
    private readonly ConcurrentDictionary<string, (PcbLibrary Library, List<PcbComponent> Components, Dictionary<int, string> LayerNames, string Name, long Seq)> _items = new();
    private long _seq;

    public string Add(PcbLibrary library, List<PcbComponent> components, Dictionary<int, string> layerNames, string name)
    {
        var id = Guid.NewGuid().ToString("N");
        _items[id] = (library, components, layerNames, name, Interlocked.Increment(ref _seq));
        while (_items.Count > Capacity)
        {
            var oldest = _items.OrderBy(kv => kv.Value.Seq).First().Key;
            _items.TryRemove(oldest, out _);
        }
        return id;
    }

    public (PcbLibrary Library, List<PcbComponent> Components, Dictionary<int, string> LayerNames, string Name)? Get(string? id) =>
        id is not null && _items.TryGetValue(id, out var v) ? (v.Library, v.Components, v.LayerNames, v.Name) : null;
}
