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
using OriginalCircuit.Altium.Rendering.Step;
using OriginalCircuit.Altium.Rendering.Svg;
using OriginalCircuit.Altium.Serialization.Writers;
using OriginalCircuit.Eda.Enums;
using OriginalCircuit.Eda.Primitives;
using OriginalCircuit.Eda.Rendering;

var builder = WebApplication.CreateBuilder(args);

// A real organization's libraries can run well past 64MB (confirmed against a user-supplied 601-
// footprint, ~50MB library) — Kestrel's own limit and, separately, the multipart form-reader's own
// MultipartBodyLengthLimit (a second, independent cap the IFormFile binding pipeline enforces even
// once Kestrel's is raised) both need to allow it, or the upload fails at the connection level before
// our own /api/upload handler ever runs — surfacing to the browser as a bare "Failed to fetch" with
// no error body to show the user, rather than the clean JSON error a library-parsing failure gets.
const long MaxUploadBytes = 512L * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);
builder.Services.AddSingleton<LibraryCache>();

var app = builder.Build();
app.UseDefaultFiles();   // serve wwwroot/index.html at "/"
app.UseStaticFiles();

var svg = new SvgRenderer();

// mm constants rather than pre-built Coord values: top-level-statement files can't hold static
// readonly fields for a static local function to capture (static locals capture nothing at all), and
// Coord.FromMm is cheap enough to just call at each use site. Declared here (ahead of the endpoints
// that reference them) because top-level-statement locals must be declared before use, unlike fields.
const double DefaultBodyOffsetMm = 0.15;   // courtyard keepout margin outside the body, when unspecified
const double DefaultPadOffsetMm = 0.15;    // courtyard keepout margin outside pads, when unspecified
const double DefaultSmoothingMm = 0.15;    // STEP-outline simplification tolerance, when unspecified —
                                            // deliberately looser than the rasterization grid itself, so
                                            // the traced shape reads as a general "boxy" outline rather
                                            // than tracing every fillet/notch in the real 3D geometry
const double DrawingLineWidthMm = 0.1;     // assembly + courtyard track/ring width
const double Pin1EdgeMarginMm = 0.1;       // gap kept between the ring and the pad's own edge
const double DesignatorStrokeRatio = 3.0 / 20.0;  // height:stroke-width = 20:3
const double DesignatorFitMargin = 0.92;   // small margin so text doesn't touch the edges
const double DesignatorTargetChars = 4;    // size for ~4 characters across the X extent — the
                                            // ".Designator" placeholder is longer and is expected to
                                            // overflow X; a real resolved designator (R1, C23, ...) won't
const double DesignatorMaxHeightMils = 40; // hard cap regardless of how much Y-room is available

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
    var contentWarnings = ContentWarnings(component, layerNames, new HashSet<int>(req.LegalLayers ?? new List<int>()));

    return Results.Json(new { svg = svgText, layers, contentWarnings });
});

// Content warnings only, no SVG re-render — the Legal Layers checklist calls this whenever the legal
// set changes so ContentWarnings' name-based resolution can be recomputed against the new set (a
// duplicate-name ambiguity resolves differently once a layer's legal flag flips), without the pan/zoom
// reset a full /api/render.svg round-trip would cause.
app.MapPost("/api/content-warnings", (ContentWarningsRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (_, components, layerNames, _) = entry.Value;
    if (req.Index < 0 || req.Index >= components.Count)
        return Results.BadRequest(new { error = "Footprint index out of range." });

    var component = components[req.Index];
    var contentWarnings = ContentWarnings(component, layerNames, new HashSet<int>(req.LegalLayers ?? new List<int>()));
    return Results.Json(new { contentWarnings });
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
        var warnings = ContentWarnings(component, layerNames, legal);

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

// ── Generate Courtyard: for footprints missing this documentation outright (not just using the wrong
// layer for it — see ContentWarnings for that case). Draws a keepout outline onto the footprint's
// mount-side Courtyard layer, replacing whatever was there before — by default the body's true
// top-down STEP silhouette (expanded by BodyOffsetMm) unioned with each pad's extent (expanded by
// PadOffsetMm), or a boxy rectangle union of the same when SimpleMode is set / no body has a usable
// STEP model.
app.MapPost("/api/generate/courtyard", (GenerateCourtyardRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (library, components, layerNames, _) = entry.Value;
    if (req.Index < 0 || req.Index >= components.Count)
        return Results.BadRequest(new { error = "Footprint index out of range." });

    var (ok, message) = GenerateCourtyard(components[req.Index], layerNames, library.Models,
        req.BodyOffsetMm ?? DefaultBodyOffsetMm, req.PadOffsetMm ?? DefaultPadOffsetMm,
        req.SmoothingMm ?? DefaultSmoothingMm, req.SimpleMode, new HashSet<int>(req.LegalLayers ?? new List<int>()));
    return Results.Json(new { ok, message });
});

// ── Generate Assembly Outline: traces the 3D body's true top-down STEP silhouette (or, when
// SimpleMode is set / there's no usable STEP model, the stored 3D body outline — or, when there's no
// body at all, a boxy union of the pad extents instead) onto the footprint's mount-side Assembly
// layer, replacing whatever was there. Optionally adds a centered ".Designator" special string sized
// to fit inside it.
app.MapPost("/api/generate/assembly", (GenerateAssemblyRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (library, components, layerNames, _) = entry.Value;
    if (req.Index < 0 || req.Index >= components.Count)
        return Results.BadRequest(new { error = "Footprint index out of range." });

    var (ok, message) = GenerateAssembly(components[req.Index], layerNames, req.IncludeDesignator, library.Models,
        req.SmoothingMm ?? DefaultSmoothingMm, req.SimpleMode, new HashSet<int>(req.LegalLayers ?? new List<int>()));
    return Results.Json(new { ok, message });
});

// ── Generate Pin-1 Indicator: adds a ring around pin 1's pad to the mount-side Assembly layer.
// Deliberately its own action, separate from the outline above (not every footprint needs one, and
// it doesn't clear the layer first — it's meant to be added onto an outline, not replace one).
app.MapPost("/api/generate/pin1", (GenerateRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (_, components, layerNames, _) = entry.Value;
    if (req.Index < 0 || req.Index >= components.Count)
        return Results.BadRequest(new { error = "Footprint index out of range." });

    var (ok, message) = GeneratePin1Indicator(components[req.Index], layerNames, new HashSet<int>(req.LegalLayers ?? new List<int>()));
    return Results.Json(new { ok, message });
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
// legalLayers: when a name is ambiguous (a library where two different layer ids share the same
// name — e.g. someone renamed a layer without noticing another already had that name), prefer
// whichever candidate is currently marked legal, so name-based resolution lands on the layer the
// user actually curated rather than an arbitrary same-named one. If NONE of the candidates are legal
// yet (including the common case of a brand-new upload, before the user has touched the Legal Layers
// checklist at all — every mechanical layer starts out unmarked), the name is left unresolved rather
// than guessing: picking an arbitrary duplicate was exactly what produced spurious "is empty" content
// warnings and wrong-layer generation targets on the *other*, correct duplicate. A non-ambiguous name
// (only one id has it) always resolves regardless of legal state — there's nothing to disambiguate.
static Dictionary<string, int> LayersByName(Dictionary<int, string> layerNames, HashSet<int>? legalLayers = null)
{
    var byNameAll = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
    foreach (var (id, name) in layerNames)
    {
        if (string.IsNullOrWhiteSpace(name)) continue;
        var key = name.Trim();
        if (!byNameAll.TryGetValue(key, out var ids)) byNameAll[key] = ids = new List<int>();
        ids.Add(id);
    }

    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    foreach (var (name, ids) in byNameAll)
    {
        var preferred = legalLayers is null ? 0 : ids.FirstOrDefault(legalLayers.Contains);
        if (preferred != 0) map[name] = preferred;
        else if (ids.Count == 1) map[name] = ids[0];
        // else: ambiguous and no legal candidate identified — leave unresolved.
    }
    return map;
}

// Advisory content checks, independent of the legal/illegal layer audit — these catch a footprint
// that only uses legal layers but still has something wrong that "is this layer legal" can never see:
// a stray track on the 3D-body layer, a missing 3D body, a 3D body on the wrong layer, an empty
// courtyard/assembly outline. Keyed off the conventional names above rather than raw ids; a check is
// skipped entirely (not flagged) if the library never named the corresponding layer, since there's
// nothing to check against.
static List<ContentWarning> ContentWarnings(PcbComponent c, Dictionary<int, string> layerNames, HashSet<int>? legalLayers = null)
{
    var byName = LayersByName(layerNames, legalLayers);
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

// ── Courtyard / assembly-outline / pin-1 generation ─────────────────────────────────────────────
// For footprints missing this documentation outright, as opposed to ContentWarnings above which
// catches it existing but on the wrong layer. All three actions target whichever named layer matches
// MountSide's guess ("bottom" only when the evidence is unambiguous; "top" for "top", "both", or
// unknown — a courtyard/assembly still has to go somewhere, and top is the default authoring side).

static int? TargetLayer(Dictionary<string, int> byName, string? side, string topName, string bottomName) =>
    byName.TryGetValue(side == "bottom" ? bottomName : topName, out var id) ? id : null;

// A target name can come back unresolved from LayersByName for two different reasons — the library
// genuinely has no layer with that name, or it has more than one (a duplicate) and none is marked
// legal yet. Worth telling apart: the second case has a fix (mark the right one legal), the first
// doesn't.
static string LayerNotFoundMessage(Dictionary<int, string> layerNames, string targetName) =>
    layerNames.Values.Count(v => string.Equals(v?.Trim(), targetName, StringComparison.OrdinalIgnoreCase)) > 1
        ? $"Multiple layers are named '{targetName}' and none are marked legal yet — mark the correct one legal in the Legal Layers panel, then try again."
        : $"This library doesn't define a '{targetName}' layer — nothing to generate onto.";

static (bool Ok, string Message) GenerateCourtyard(
    PcbComponent c, Dictionary<int, string> layerNames, List<PcbModel> models,
    double bodyOffsetMm, double padOffsetMm, double smoothingMm, bool simpleMode, HashSet<int> legalLayers)
{
    var byName = LayersByName(layerNames, legalLayers);
    var side = MountSide(c,
        byName.TryGetValue("Top 3D Body", out var tb) ? tb : null,
        byName.TryGetValue("Bottom 3D Body", out var bb) ? bb : null);
    var targetName = side == "bottom" ? "Bottom Courtyard" : "Top Courtyard";
    var targetId = TargetLayer(byName, side, "Top Courtyard", "Bottom Courtyard");
    if (targetId is null)
        return (false, LayerNotFoundMessage(layerNames, targetName));

    var bodyOffset = Coord.FromMm(bodyOffsetMm);
    var bodies = c.ComponentBodies.Cast<PcbComponentBody>().Where(b => !b.Bounds.IsEmpty).ToList();
    var padRects = c.Pads.Cast<PcbPad>().Where(p => !p.Bounds.IsEmpty).Select(p => p.Bounds).ToList();
    var offsetPadRects = padRects.Select(r => r.Inflate(Coord.FromMm(padOffsetMm))).ToList();

    if (bodies.Count == 0 && padRects.Count == 0)
        return (false, "Nothing to base a courtyard on — this footprint has no 3D body and no pads.");

    var projectedCount = 0;
    List<List<CoordPoint>>? stepLoops = null;
    if (bodies.Count > 0 && !simpleMode)
    {
        // Traces each body's true top-down STEP silhouette (dilated by bodyOffsetMm via distance-
        // transform, not just its bounding box) unioned with the offset pad rects — a real offset
        // outline, not a rectangle standing in for the body. Bodies without a usable STEP model still
        // contribute their bounding rect inside the same combined outline.
        (stepLoops, projectedCount) = StepBodyOutline.TryProjectCourtyardOutline(
            bodies, models, bodyOffsetMm, offsetPadRects, smoothingMm, StepBodyOutline.CreateCache());
    }

    var usedStepOutline = stepLoops is { Count: > 0 };
    List<List<CoordPoint>> loops;
    if (usedStepOutline)
    {
        loops = stepLoops!;
    }
    else if (bodies.Count > 0)
    {
        // Simple mode, or no body had a usable STEP model to project: the exact (non-rasterized)
        // rectangle union — each body's bounding box, each pad, all inflated by their own offset.
        var rects = bodies.Select(b => b.Bounds.Inflate(bodyOffset)).Concat(offsetPadRects).ToList();
        loops = BoxyUnionOutline(rects);
    }
    else
    {
        // No body: pads are the only evidence, and a boxy union of individually-inflated pad rects can
        // come out disjointed (e.g. a 2-pad passive with a gap between pads) — one continuous box
        // around the outermost pads is what was actually asked for here.
        loops = SingleBoxOutline(CoordRect.Union(offsetPadRects));
    }

    if (loops.Count == 0)
        return (false, "Could not compute a courtyard outline.");

    var cleared = ClearLayer(c, targetId.Value);
    var lineWidth = Coord.FromMm(DrawingLineWidthMm);
    var segCount = loops.Sum(loop => DrawClosedLoop(c, loop, targetId.Value, lineWidth));

    var basis = simpleMode
        ? " (simple mode — bounding box only)"
        : usedStepOutline
            ? $" — body traced from STEP projection ({projectedCount}/{bodies.Count} bodies)"
            : "";
    return (true, $"Generated {targetName}: {segCount} segment(s) across {loops.Count} outline(s){basis}, replacing {cleared} old item(s).");
}

static (bool Ok, string Message) GenerateAssembly(
    PcbComponent c, Dictionary<int, string> layerNames, bool includeDesignator, List<PcbModel> models,
    double smoothingMm, bool simpleMode, HashSet<int> legalLayers)
{
    var byName = LayersByName(layerNames, legalLayers);
    var side = MountSide(c,
        byName.TryGetValue("Top 3D Body", out var tb) ? tb : null,
        byName.TryGetValue("Bottom 3D Body", out var bb) ? bb : null);
    var targetName = side == "bottom" ? "Bottom Assembly" : "Top Assembly";
    var targetId = TargetLayer(byName, side, "Top Assembly", "Bottom Assembly");
    if (targetId is null)
        return (false, LayerNotFoundMessage(layerNames, targetName));

    // A body only counts as usable ground truth once we know its shape one way or another: either a
    // real STEP model we can project top-down (the accurate case, skipped entirely in simple mode —
    // the checkbox exists precisely to fall back to the plain stored Outline this used before STEP
    // projection existed), or a non-degenerate stored Outline to fall back on otherwise (no ModelId,
    // unsupported STEP feature, ...).
    var allBodies = c.ComponentBodies.Cast<PcbComponentBody>().ToList();
    var projCache = StepBodyOutline.CreateCache();
    var perBody = allBodies
        .Select(b => (Body: b, Projected: simpleMode ? null : StepBodyOutline.TryProjectTopDown(b, models, projCache, smoothingMm)))
        .Where(x => x.Projected is { Count: > 0 } || x.Body.Outline.Count >= 3)
        .ToList();

    List<List<CoordPoint>> loops;
    CoordRect bbox;
    string source;

    if (perBody.Count > 0)
    {
        // Prefer the true top-down STEP silhouette per body — an accurate trace of the real 3D
        // geometry, not the rough polygon Altium stores in Outline — falling back to that stored
        // Outline only for bodies whose model couldn't be projected. Unlike the courtyard, there's no
        // "boxy" simplification here, per the original spec ("a shape of the 3D body"); either source
        // already IS the shape.
        loops = perBody.SelectMany(x => x.Projected ?? new List<List<CoordPoint>> { x.Body.Outline.ToList() }).ToList();
        bbox = CoordRect.Union(perBody.Select(x => x.Projected is { Count: > 0 } proj
            ? CoordRect.Union(proj.SelectMany(loop => loop).Select(p => new CoordRect(p, p)))
            : x.Body.Bounds));
        var projectedCount = perBody.Count(x => x.Projected is { Count: > 0 });
        source = simpleMode
            ? "3D body outline (simple mode)"
            : projectedCount == perBody.Count
                ? "STEP model projection"
                : projectedCount > 0
                    ? $"STEP model projection ({projectedCount}/{perBody.Count} bodies) + stored 3D body outline"
                    : "3D body outline";
    }
    else
    {
        // No body: one continuous box around the outermost pads, not a per-pad boxy union — a 2-pad
        // passive with a gap between pads should still get a single rectangle, not two disjoint shapes.
        var padRects = c.Pads.Cast<PcbPad>().Where(p => !p.Bounds.IsEmpty).Select(p => p.Bounds).ToList();
        if (padRects.Count == 0)
            return (false, "Nothing to base an assembly outline on — this footprint has no 3D body and no pads.");
        bbox = CoordRect.Union(padRects);
        loops = SingleBoxOutline(bbox);
        if (loops.Count == 0) return (false, "Could not compute an assembly outline from pads.");
        source = "pad outline (no 3D body found)";
    }

    // Unchecking "Include .Designator" means leave whatever designator text is already there alone —
    // it's specifically excluded from the clear, not wiped and left absent.
    var cleared = ClearLayer(c, targetId.Value, preserveDesignatorText: !includeDesignator);
    var segCount = loops.Sum(loop => DrawClosedLoop(c, loop, targetId.Value, Coord.FromMm(DrawingLineWidthMm)));

    var extra = "";
    if (includeDesignator && AddDesignatorText(c, targetId.Value, bbox))
        extra = " + .Designator text";

    return (true, $"Generated {targetName} from {source}: {segCount} segment(s) across {loops.Count} outline(s){extra}, replacing {cleared} old item(s).");
}

static (bool Ok, string Message) GeneratePin1Indicator(PcbComponent c, Dictionary<int, string> layerNames, HashSet<int> legalLayers)
{
    var pin1 = c.Pads.Cast<PcbPad>().FirstOrDefault(p => (p.Designator ?? "").Trim() == "1");
    if (pin1 is null)
        return (false, "No pad numbered '1' found — nothing to mark.");

    var byName = LayersByName(layerNames, legalLayers);
    var side = MountSide(c,
        byName.TryGetValue("Top 3D Body", out var tb) ? tb : null,
        byName.TryGetValue("Bottom 3D Body", out var bb) ? bb : null);
    var targetName = side == "bottom" ? "Bottom Assembly" : "Top Assembly";
    var targetId = TargetLayer(byName, side, "Top Assembly", "Bottom Assembly");
    if (targetId is null)
        return (false, LayerNotFoundMessage(layerNames, targetName));

    // Inscribed inside the pad, not surrounding it — sized off the SMALLER pad dimension so the ring
    // clears every edge (a wide/short pad would otherwise let a width-based radius poke past the top
    // and bottom), with a floor so a very small pad still gets a visible, non-degenerate ring. The
    // ring is drawn as a centerline arc with its own stroke width, so its ink extends half that width
    // beyond the center radius — without subtracting that half-width too, the margin only accounted
    // for the centerline and the visible ring could poke past the intended clearance (or the pad edge
    // on a small pad).
    var padBounds = pin1.Bounds;
    var minDim = Coord.Min(padBounds.Width, padBounds.Height);
    var ringWidth = Coord.FromMm(DrawingLineWidthMm);
    var radius = Coord.Max(minDim / 2 - Coord.FromMm(Pin1EdgeMarginMm) - ringWidth / 2, Coord.FromMm(0.05));
    c.AddArc(PcbArc.Create()
        .Center(pin1.Location.X, pin1.Location.Y)
        .Radius(radius)
        .Width(ringWidth)
        .OnLayer(targetId.Value)
        .FullCircle()
        .Build());

    return (true, $"Added a pin-1 ring on {targetName} around pad '{pin1.Designator}'. Click again to add another — this doesn't check for an existing one.");
}

// Removes every primitive of every kind on the given layer — "generating" a drawing replaces whatever
// was there rather than adding to it (per the original request: clear the layer of old primitives and
// designator text first). preserveDesignatorText skips any text with IsDesignator set — used when the
// caller isn't (re)generating a designator string itself, so unchecking "Include .Designator" doesn't
// delete one that was already there.
static int ClearLayer(PcbComponent c, int layerId, bool preserveDesignatorText = false)
{
    var removed = 0;
    foreach (var t in c.Tracks.Cast<PcbTrack>().Where(x => x.Layer == layerId).ToList()) { c.RemoveTrack(t); removed++; }
    foreach (var a in c.Arcs.Cast<PcbArc>().Where(x => x.Layer == layerId).ToList()) { c.RemoveArc(a); removed++; }
    foreach (var f in c.Fills.Cast<PcbFill>().Where(x => x.Layer == layerId).ToList()) { c.RemoveFill(f); removed++; }
    foreach (var r in c.Regions.Cast<PcbRegion>().Where(x => x.Layer == layerId).ToList()) { c.RemoveRegion(r); removed++; }
    foreach (var tx in c.Texts.Cast<PcbText>().Where(x => x.Layer == layerId && !(preserveDesignatorText && x.IsDesignator)).ToList()) { c.RemoveText(tx); removed++; }
    foreach (var p in c.Pads.Cast<PcbPad>().Where(x => x.Layer == layerId).ToList()) { c.RemovePad(p); removed++; }
    foreach (var v in c.Vias.Cast<PcbVia>().Where(x => x.Layer == layerId).ToList()) { c.RemoveVia(v); removed++; }
    foreach (var b in c.ComponentBodies.Cast<PcbComponentBody>().Where(x => x.Layer == layerId).ToList()) { c.RemoveComponentBody(b); removed++; }
    return removed;
}

// Draws a closed polygon as a chain of straight tracks, wrapping the last point back to the first.
static int DrawClosedLoop(PcbComponent c, List<CoordPoint> loop, int layerId, Coord width)
{
    if (loop.Count < 2) return 0;
    var n = 0;
    for (int i = 0; i < loop.Count; i++)
    {
        var a = loop[i];
        var b = loop[(i + 1) % loop.Count];
        if (a.Equals(b)) continue;
        c.AddTrack(PcbTrack.Create().From(a.X, a.Y).To(b.X, b.Y).Width(width).OnLayer(layerId).Build());
        n++;
    }
    return n;
}

// Adds a centered ".Designator" special string. This placeholder text is a stand-in for whatever
// short designator (R1, C23, U5, ...) Altium substitutes once the footprint is actually placed on a
// design, so it's sized for THAT, not for its own literal length: height is capped by the box's
// Y-extent (must fit fully) and by fitting ~DesignatorTargetChars average-width characters across the
// X-extent (a real 2-4 character designator will fit; the 11-character ".Designator" placeholder
// itself is expected to overflow X — that's fine), and by an absolute 40 mil ceiling regardless of how
// much room the box has. Stroke width follows the requested 20:3 height:width ratio.
//
// Location is set to the box's CENTER, with Justification=CenterCenter — not a manually-computed
// bottom-left corner for the placeholder string's own width. That distinction matters once Altium
// substitutes the real (shorter, variable-length) designator: with true center justification Altium
// re-centers it around Location automatically, same as it does live in the editor; a bottom-left
// anchor sized for ".Designator" would leave a short real designator visibly offset instead of
// centered. Confirmed via a user-supplied test library that Justification (stored at the same byte as
// the historically inverted-rectangle-only InvertedRectJustification) governs plain text the same way.
//
// Returns false (no-op) if the box is degenerate or the font has nothing to lay out.
static bool AddDesignatorText(PcbComponent c, int layerId, CoordRect bbox)
{
    const string designatorText = ".Designator";
    var style = AltiumStrokeFont.FromStrokeFont(PcbStrokeFont.Default);
    var segments = AltiumStrokeFont.Layout(designatorText, style, out var advanceWidth);
    if (segments.Count == 0 || advanceWidth <= 0 || bbox.IsEmpty) return false;

    var avgCharWidth = advanceWidth / designatorText.Length;
    var heightFromY = bbox.Height * DesignatorFitMargin;
    var heightFromX = bbox.Width * DesignatorFitMargin / (DesignatorTargetChars * avgCharWidth);
    var height = Coord.Min(Coord.Min(heightFromY, heightFromX), Coord.FromMils(DesignatorMaxHeightMils));
    if (height <= Coord.Zero) return false;

    var text = PcbText.Create(designatorText)
        .At(bbox.Center.X, bbox.Center.Y)
        .Height(height)
        .StrokeWidth(height * DesignatorStrokeRatio)
        .OnLayer(layerId)
        .Build();
    text.IsDesignator = true;
    text.TextKind = PcbTextKind.Stroke;
    text.UnderlyingString = designatorText;
    text.InvertedRectJustification = PcbTextJustification.CenterCenter;
    // The justification byte is only honored when this companion flag says it's meaningful —
    // confirmed against a user-supplied 8-component ground-truth library where every real,
    // Altium-authored text record had this set to true regardless of which justification it used.
    // Without it, Altium silently falls back to bottom-left anchoring no matter what
    // InvertedRectJustification says — exactly the bug this was written to fix.
    text.IsJustificationValid = true;
    c.AddText(text);
    return true;
}

// A single rectangle expressed as one closed 4-point loop — the "continuous box around the outermost
// pads" fallback (as opposed to BoxyUnionOutline's per-rectangle shape-following, which can come out
// disjointed when there's no body to anchor the pads together, e.g. a 2-pad passive with a gap
// between its pads). Empty box → no loops.
static List<List<CoordPoint>> SingleBoxOutline(CoordRect box)
{
    if (box.IsEmpty) return new();
    return new() { new List<CoordPoint> { box.Min, new(box.Max.X, box.Min.Y), box.Max, new(box.Min.X, box.Max.Y) } };
}

// Computes the outer boundary of the union of a set of axis-aligned rectangles as one or more closed
// rectilinear polygons — straight horizontal/vertical edges only, no arcs, no exact-shape hugging (the
// "boxy" courtyard/pad-fallback outline). Standard "grid + boundary edge" contour trace: coordinate-
// compress into a grid of cells, mark which cells are covered by at least one input rectangle, then
// any cell-boundary edge with exactly one covered side is part of the contour; walk those edges (each
// emitted with a consistent counter-clockwise orientation, so every boundary vertex has exactly one
// outgoing edge) into closed loops, then collapse collinear runs into single segments.
// Known limitation: a shape that touches itself at exactly one point (e.g. two rectangles meeting only
// at a corner) can produce an ambiguous vertex with two valid outgoing edges; the last one processed
// wins. Not expected for real body/pad geometry.
static List<List<CoordPoint>> BoxyUnionOutline(IReadOnlyList<CoordRect> rects)
{
    var real = rects.Where(r => !r.IsEmpty).ToList();
    if (real.Count == 0) return new();

    var xs = real.SelectMany(r => new[] { r.Min.X, r.Max.X }).Distinct().OrderBy(x => x).ToList();
    var ys = real.SelectMany(r => new[] { r.Min.Y, r.Max.Y }).Distinct().OrderBy(y => y).ToList();
    int nx = xs.Count - 1, ny = ys.Count - 1;
    if (nx < 1 || ny < 1) return new();

    var filled = new bool[nx, ny];
    for (int i = 0; i < nx; i++)
    {
        var midX = xs[i] + (xs[i + 1] - xs[i]) / 2;
        for (int j = 0; j < ny; j++)
        {
            var midY = ys[j] + (ys[j + 1] - ys[j]) / 2;
            var p = new CoordPoint(midX, midY);
            filled[i, j] = real.Any(r => r.Contains(p));
        }
    }

    // Each boundary edge is emitted walking counter-clockwise around its filled cell, so every vertex
    // ends up with exactly one outgoing edge (the invariant the loop-walk below relies on).
    var outgoing = new Dictionary<CoordPoint, CoordPoint>();
    for (int i = 0; i < nx; i++)
    for (int j = 0; j < ny; j++)
    {
        if (!filled[i, j]) continue;
        if (i == 0 || !filled[i - 1, j]) outgoing[new(xs[i], ys[j])] = new(xs[i], ys[j + 1]);                 // left edge, upward
        if (i == nx - 1 || !filled[i + 1, j]) outgoing[new(xs[i + 1], ys[j + 1])] = new(xs[i + 1], ys[j]);    // right edge, downward
        if (j == 0 || !filled[i, j - 1]) outgoing[new(xs[i + 1], ys[j])] = new(xs[i], ys[j]);                 // bottom edge, leftward
        if (j == ny - 1 || !filled[i, j + 1]) outgoing[new(xs[i], ys[j + 1])] = new(xs[i + 1], ys[j + 1]);    // top edge, rightward
    }

    var visited = new HashSet<CoordPoint>();
    var loops = new List<List<CoordPoint>>();
    foreach (var start in outgoing.Keys)
    {
        if (visited.Contains(start)) continue;
        var raw = new List<CoordPoint> { start };
        var cur = start;
        while (true)
        {
            visited.Add(cur);
            var next = outgoing[cur];
            if (next.Equals(start)) break;
            raw.Add(next);
            cur = next;
        }
        if (raw.Count >= 3) loops.Add(SimplifyCollinear(raw));
    }
    return loops;
}

// Merges consecutive collinear points on a closed (circular) rectilinear polyline into single
// segments, so a long straight run of grid cells becomes one track instead of many tiny ones.
static List<CoordPoint> SimplifyCollinear(List<CoordPoint> pts)
{
    if (pts.Count < 3) return pts;
    var result = new List<CoordPoint>();
    int n = pts.Count;
    for (int i = 0; i < n; i++)
    {
        var prev = pts[(i - 1 + n) % n];
        var cur = pts[i];
        var next = pts[(i + 1) % n];
        var sameLine = (prev.X == cur.X && cur.X == next.X) || (prev.Y == cur.Y && cur.Y == next.Y);
        if (!sameLine) result.Add(cur);
    }
    return result.Count >= 3 ? result : pts;
}

// One content-sanity finding from ContentWarnings. LayerId/Kind are set only when the warning is
// directly actionable via reassignment — the front-end uses them to reveal that specific layer's
// reassignment dropdown for that specific kind, exactly like an illegal-layer flag does — and left
// null when the fix isn't a reassignment (nothing missing has anywhere to reassign FROM).
record ContentWarning(string Message, int? LayerId, string? Kind);

// The render request sent by the front-end. LegalLayers (the same checked set the audit report and
// generation endpoints use) lets ContentWarnings' name-based layer resolution prefer the layer the
// user actually marked legal when a name like "Top Courtyard" is ambiguous (two ids sharing it) —
// otherwise an unused, not-legal duplicate could get flagged "empty" instead of the real one.
record RenderRequest(string Id, int Index, int? Width, int? Height, List<int>? LegalLayers);

// The content-warnings-only request sent whenever the Legal Layers checklist changes (see
// /api/content-warnings above) — same idea as RenderRequest but without the SVG-only Width/Height.
record ContentWarningsRequest(string Id, int Index, List<int>? LegalLayers);

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

// The pin-1 generation request: the footprint to act on, plus which layer ids the user currently
// has marked legal (see GenerateCourtyardRequest's remark — same reason all three generation
// requests carry this).
record GenerateRequest(string Id, int Index, List<int>? LegalLayers);

// The courtyard generation request: BodyOffsetMm/PadOffsetMm are the keepout clearances (independent
// so the body's true offset outline and the pad clearance can differ); SmoothingMm is the STEP-outline
// simplification tolerance; SimpleMode skips STEP projection entirely and falls back to the plain
// bounding-box behavior generation used before it existed. Null fields fall back to the Default*Mm
// constants server-side. LegalLayers is the same set the audit report uses (checked layer ids from
// the frontend's Legal Layers list) -- needed because a library can have two different layer ids
// sharing the same name (e.g. "Top Courtyard"); when that happens, name-based target resolution
// prefers whichever one is marked legal instead of an arbitrary same-named layer (see LayersByName).
record GenerateCourtyardRequest(string Id, int Index, double? BodyOffsetMm, double? PadOffsetMm, double? SmoothingMm, bool SimpleMode, List<int>? LegalLayers);

// The assembly-outline generation request: whether to also add a centered ".Designator" string, the
// STEP-outline simplification tolerance, whether to skip STEP projection (see SimpleMode above), and
// LegalLayers (see GenerateCourtyardRequest's remark).
record GenerateAssemblyRequest(string Id, int Index, bool IncludeDesignator, double? SmoothingMm, bool SimpleMode, List<int>? LegalLayers);

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
