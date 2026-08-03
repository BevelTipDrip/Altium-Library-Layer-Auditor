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

    List<PcbComponent> components;
    Dictionary<int, string> layerNames;
    try
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        var library = await AltiumLibrary.OpenPcbLibAsync(buffer, ct);
        components = library.AllComponents.Cast<PcbComponent>().ToList();

        // Layer numbering is fixed (1=Top, 32=Bottom, 57-72=Mechanical, ...), but the DISPLAY name for
        // a given number is per-file: Altium lets a user rename e.g. layer 69 to "Top Assembly" or "Top
        // Courtyard", and that only lives in this library's own layer-stack header — so it must be read
        // fresh from each upload, never assumed from a fixed table.
        layerNames = (library as PcbLibrary)?.LayerStack?.Layers
            .ToDictionary(l => l.Index, l => l.Name) ?? new Dictionary<int, string>();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Could not read '{file.FileName}': {ex.Message}" });
    }

    var id = cache.Add(components, layerNames);
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
    var (components, layerNames) = entry.Value;
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

    var layers = layerIds.Select(id => new
    {
        id,
        name = FormatLayerName(id, layerNames.TryGetValue(id, out var customName) ? customName : LayerColors.GetName(id)),
        color = ToHex(LayerColors.GetColor(id)),
    });

    return Results.Json(new { svg = svgText, layers });
});

// ── Audit report: components with primitives/3D bodies on layers not in the "legal" set ──────
app.MapPost("/api/report", (ReportRequest req, LibraryCache cache) =>
{
    var entry = cache.Get(req.Id);
    if (entry is null) return Results.NotFound(new { error = "Library not found — re-upload it." });
    var (components, layerNames) = entry.Value;
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

app.Run();

static string ToHex(uint argb) => $"#{(argb >> 16) & 0xFF:X2}{(argb >> 8) & 0xFF:X2}{argb & 0xFF:X2}";

// Standard layers (copper, silk, paste, solder, drill, multi-layer, ...) use the same small integer
// in this reader's internal IDs as Altium's own UI/scripting refer to them by, so showing the id
// verbatim is correct and familiar. Mechanical layers are different: this reader's internal IDs pack
// the original 16 mechanical slots into 57-72, and an extended 17-32 range into 83-98 (both this file
// format's own offsets — see PcbLibReader.ResolveExtendedMechanicalLayer), but Altium's UI and
// scripting API call them "Mechanical 1" through "Mechanical 32" — the number a user actually
// recognizes and compares between libraries (e.g. "Courtyard on Mechanical 15 in one library vs.
// Mechanical 16 in another"). Convert back to that 1-32 index so the bracketed number means what a
// human auditing the library expects, not this reader's internal offset.
static int DisplayLayerNumber(int id) => id switch
{
    >= 57 and <= 72 => id - 56,   // Mechanical 1-16
    >= 83 and <= 98 => id - 66,   // Mechanical 17-32 (extended range)
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

// A tiny bounded in-memory cache of parsed libraries, so switching footprints re-renders without
// re-uploading. LayerNames is that file's own layer-stack table (layer id -> display name), captured
// once at upload time since it's per-file, not a fixed global table.
sealed class LibraryCache
{
    private const int Capacity = 8;
    private readonly ConcurrentDictionary<string, (List<PcbComponent> Components, Dictionary<int, string> LayerNames, long Seq)> _items = new();
    private long _seq;

    public string Add(List<PcbComponent> components, Dictionary<int, string> layerNames)
    {
        var id = Guid.NewGuid().ToString("N");
        _items[id] = (components, layerNames, Interlocked.Increment(ref _seq));
        while (_items.Count > Capacity)
        {
            var oldest = _items.OrderBy(kv => kv.Value.Seq).First().Key;
            _items.TryRemove(oldest, out _);
        }
        return id;
    }

    public (List<PcbComponent> Components, Dictionary<int, string> LayerNames)? Get(string? id) =>
        id is not null && _items.TryGetValue(id, out var v) ? (v.Components, v.LayerNames) : null;
}
