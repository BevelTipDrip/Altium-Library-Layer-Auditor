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

    // Every layer used anywhere in the library — the master list the front-end builds its
    // "Legal Layers" checklist from. Mechanical layers (57-72) default to unchecked/illegal since
    // that's exactly where Altium's layer-Type-vs-number mismatches happen between libraries; the
    // fixed-purpose layers (copper, silk, paste, solder, ...) default to legal.
    var libraryLayers = components
        .SelectMany(AuditEntries)
        .Select(e => e.Layer)
        .Distinct()
        .OrderBy(l => l)
        .Select(l => new
        {
            id = l,
            name = FormatLayerName(l, layerNames.TryGetValue(l, out var n) ? n : LayerColors.GetName(l)),
            color = ToHex(LayerColors.GetColor(l)),
            mechanical = PcbLayerGroups.IsMechanical(l),
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

    return Results.Json(new { svg = svgText, layers });
});

// ── Audit report: components with primitives/3D bodies on layers not in the "legal" set ──────
app.MapPost("/api/report", (ReportRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (_, components, layerNames, _) = entry.Value;
    var legal = new HashSet<int>(req.LegalLayers ?? new List<int>());

    string NameOf(int id) => FormatLayerName(id, layerNames.TryGetValue(id, out var n) ? n : LayerColors.GetName(id));

    var componentReports = new List<object>();
    var totalIssues = 0;
    var body = new StringBuilder();

    foreach (var component in components)
    {
        var issues = AuditEntries(component)
            .Where(e => !legal.Contains(e.Layer))
            .GroupBy(e => (e.Layer, e.Kind))
            .Select(g => new { layerId = g.Key.Layer, layerName = NameOf(g.Key.Layer), kind = g.Key.Kind, count = g.Count() })
            .OrderBy(i => i.layerId).ThenBy(i => i.kind)
            .ToList();

        if (issues.Count == 0) continue;

        totalIssues += issues.Sum(i => i.count);
        componentReports.Add(new { name = component.Name, issues });

        body.AppendLine();
        body.AppendLine(string.IsNullOrWhiteSpace(component.Name) ? "(unnamed)" : component.Name);
        foreach (var i in issues)
            body.AppendLine($"    {i.count,4}  {i.kind,-10} {i.layerName}");
    }

    var header = componentReports.Count == 0
        ? "No illegal layer usage found."
        : $"{componentReports.Count} component(s) with illegal layer usage — {totalIssues} issue(s) total.";

    var text = $"Illegal Layer Usage Report\n{new string('=', 40)}\n{header}\n{body}";

    return Results.Json(new
    {
        componentCount = componentReports.Count,
        totalIssues,
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

// The render request sent by the front-end.
record RenderRequest(string Id, int Index, int? Width, int? Height);

// The audit-report request sent by the front-end: which layer IDs the user has marked legal.
record ReportRequest(string Id, List<int>? LegalLayers);

// One reassignment rule: move everything of Kind ("Primitive" or "3D Body") on FromLayer to ToLayer.
record ReassignRule(int FromLayer, string Kind, int ToLayer);

// The reassignment request: apply a batch of rules to one footprint.
record ReassignRequest(string Id, int Index, List<ReassignRule> Reassignments);

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
