namespace OriginalCircuit.Altium.Models.Pcb;

/// <summary>
/// Represents a single layer entry in the PCB layer stack.
/// </summary>
public sealed class PcbLayerEntry
{
    /// <summary>
    /// Layer index (1-based).
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Layer name (e.g., "Top Layer", "Bottom Layer", "Mid-Layer 1").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Index of the previous layer in the stack.
    /// </summary>
    public int PreviousIndex { get; set; }

    /// <summary>
    /// Index of the next layer in the stack.
    /// </summary>
    public int NextIndex { get; set; }

    /// <summary>
    /// Whether copper is present on this layer.
    /// </summary>
    public bool CopperEnabled { get; set; }

    /// <summary>
    /// Dielectric material name.
    /// </summary>
    public string DielectricMaterial { get; set; } = string.Empty;

    /// <summary>
    /// Layer color as packed integer.
    /// </summary>
    public int Color { get; set; }
}

/// <summary>
/// Represents the PCB layer stack parsed from Board6 parameters.
/// Provides convenient access to layer ordering and properties.
/// </summary>
public sealed class PcbLayerStack
{
    /// <summary>
    /// Ordered list of layer entries from top to bottom.
    /// </summary>
    public IReadOnlyList<PcbLayerEntry> Layers { get; }

    private PcbLayerStack(List<PcbLayerEntry> layers)
    {
        Layers = layers;
    }

    /// <summary>
    /// Parses a layer stack from Board6 (or PcbLib Library/Data header) parameters. The layer index
    /// <c>N</c> is the same numeric PCB layer ID used everywhere else (1=Top Layer, 32=Bottom Layer,
    /// 57-72=Mechanical 1-16, etc.) — a footprint's <c>LAYER69NAME</c> entry, say, names whatever the
    /// document/library author renamed layer 69 to (e.g. "Top Assembly" or "Top Courtyard"), which need
    /// not match the generic "Mechanical N" default, so callers should always resolve names through this
    /// per-file table rather than assuming the defaults.
    /// Returns null if no layer stack data is present.
    /// </summary>
    // Fixed single-purpose layers that Altium always shows regardless of the current board/library's
    // stackup or mechanical-layer configuration — there's only ever one Top Solder, and it has no
    // PREV/NEXT chain membership and no MECHENABLED flag to check (see the enabled-gating pass below).
    private static readonly HashSet<int> AlwaysEnabledLayers = new()
    {
        1, 32,          // Top Layer, Bottom Layer
        33, 34,         // Top/Bottom Overlay
        35, 36,         // Top/Bottom Paste
        37, 38,         // Top/Bottom Solder
        55, 56,         // Drill Guide, Keep-Out Layer
        73, 74,         // Drill Drawing, Multi-Layer
        81, 82,         // Pad Holes, Via Holes
    };

    public static PcbLayerStack? FromBoardParameters(Dictionary<string, string>? parameters)
    {
        if (parameters == null)
            return null;

        var entries = new Dictionary<int, PcbLayerEntry>();

        // Scan for LAYER{N}NAME — the current (unprefixed) key. Some files only carry the older
        // V7_LAYER{N}NAME snapshot, so fall back to that when the current key is absent.
        //
        // IMPORTANT: Altium always writes a full name/property template for every possible slot here
        // — all 30 Mid-Layers, all 16 Internal Planes, all 16 classic Mechanical layers — regardless
        // of whether that slot is actually turned on for THIS board/library (confirmed against a
        // 2-layer, Top/Bottom-only library that still had LAYER2NAME.."Mid-Layer 1" through
        // LAYER31NAME.."Mid-Layer 30" fully populated). So key presence here means "this slot has a
        // name," not "this layer exists" — the enabled-gating pass below removes whichever of these
        // turn out to be template filler rather than real, currently-enabled layers.
        for (var i = 1; i <= 100; i++)
        {
            var hasCurrent = parameters.TryGetValue($"LAYER{i}NAME", out var name);
            if (!hasCurrent && !parameters.TryGetValue($"V7_LAYER{i}NAME", out name))
                continue;
            var prefix = hasCurrent ? "LAYER" : "V7_LAYER";

            var entry = new PcbLayerEntry { Index = i, Name = name };

            if (parameters.TryGetValue($"{prefix}{i}PREV", out var prev) && int.TryParse(prev, out var prevIdx))
                entry.PreviousIndex = prevIdx;
            if (parameters.TryGetValue($"{prefix}{i}NEXT", out var next) && int.TryParse(next, out var nextIdx))
                entry.NextIndex = nextIdx;
            if (parameters.TryGetValue($"{prefix}{i}COPTHICK", out var cop))
                entry.CopperEnabled = cop != "0";
            if (parameters.TryGetValue($"{prefix}{i}DIELTYPE", out var diel))
                entry.DielectricMaterial = diel;
            if (parameters.TryGetValue($"{prefix}{i}COLOR", out var color) && int.TryParse(color, out var c))
                entry.Color = c;

            entries[i] = entry;
        }

        // Signal (Mid-Layer 2-31) and Internal Plane (39-54) slots: a slot that's actually part of
        // this board/library's stackup is linked into the Top(1)→...→Bottom(32) PREV/NEXT chain; a
        // disabled template slot's PREV and NEXT both stay 0 (unlinked — confirmed against the same
        // 2-layer library: every unused Mid-Layer/Internal-Plane slot had PREV=NEXT=0, while Top and
        // Bottom pointed straight at each other). Fixed-purpose layers (Overlay, Paste, Drill Guide,
        // ...) have this same PREV=NEXT=0 signature despite always being enabled, so they're exempted
        // via AlwaysEnabledLayers rather than being swept up by this check.
        foreach (var id in entries.Keys.ToList())
        {
            if (AlwaysEnabledLayers.Contains(id)) continue;
            var isStackSlot = (id >= 2 && id <= 31) || (id >= 39 && id <= 54);
            if (!isStackSlot) continue;
            var e = entries[id];
            if (e.PreviousIndex == 0 && e.NextIndex == 0)
                entries.Remove(id);
        }

        // Mechanical 1-16 (ids 57-72): the classic table above has no enabled concept at all — every
        // mechanical slot gets a full name template the same way signal layers do. The real on/off
        // flag only exists in the "LAYER_V8_{Y}" table scanned below, so pull it out first and drop
        // whichever classic mechanical entries turn out to be disabled. Names/colors already read
        // above are kept for the ones that survive (they're the same values V8 carries, but the
        // classic table is also where a custom rename would show up if the two ever disagreed).
        var mechanicalEnabled = new HashSet<int>();
        for (var y = 0; y <= 200; y++)
        {
            if (!parameters.TryGetValue($"LAYER_V8_{y}MECHENABLED", out var enabledStr) || enabledStr != "TRUE")
                continue;
            if (!parameters.TryGetValue($"LAYER_V8_{y}LAYERID", out var layerIdStr) ||
                !long.TryParse(layerIdStr, out var layerIdRaw))
                continue;
            var mechNum = (int)(layerIdRaw & 0xFF);
            if (mechNum >= 1)
                mechanicalEnabled.Add(mechNum);
        }
        for (var mechNum = 1; mechNum <= 16; mechNum++)
        {
            if (!mechanicalEnabled.Contains(mechNum))
                entries.Remove(56 + mechNum);
        }

        // Supplementary source: the "LAYER_V8_{Y}" table — the only place a custom name for a
        // Mechanical layer past 16 (id 1000+N — see PcbLibReader.MechanicalLayerId) is recorded; the
        // classic table above has no slots past Mechanical 16. Same enabled-by-VALUE rule as above:
        // every mechanical slot up to at least 32 gets a V8 entry regardless of on/off state, so
        // MECHENABLED must be checked for "TRUE", not just presence.
        //
        // Y is NOT a fixed schema position: it's specific to each file's own layer configuration and
        // does not generalize across files (confirmed empirically — the same Y means a different layer
        // in different files). Instead, each mechanical V8 slot carries its own "LAYERID" scripting
        // identifier whose LOW BYTE reliably equals the mechanical number regardless of file or Y
        // position (e.g. LAYERID=16908308 → 16908308 & 0xFF = 20 = Mechanical 20), confirmed against
        // three independently-verified files including a live cross-check against Altium's own UI.
        for (var y = 0; y <= 200; y++)
        {
            if (!parameters.TryGetValue($"LAYER_V8_{y}MECHENABLED", out var enabledStr) || enabledStr != "TRUE")
                continue;
            if (!parameters.TryGetValue($"LAYER_V8_{y}NAME", out var v8Name))
                continue;
            if (!parameters.TryGetValue($"LAYER_V8_{y}LAYERID", out var layerIdStr) ||
                !long.TryParse(layerIdStr, out var layerIdRaw))
                continue;

            var mechNum = (int)(layerIdRaw & 0xFF);
            if (mechNum < 1)
                continue;
            var id = mechNum <= 16 ? 56 + mechNum : 1000 + mechNum; // matches PcbLibReader.MechanicalLayerId

            if (!entries.ContainsKey(id))
                entries[id] = new PcbLayerEntry { Index = id, Name = v8Name };
        }

        if (entries.Count == 0)
            return null;

        // Build ordered list by following PREV/NEXT chain
        // Find the first layer (no valid previous)
        var ordered = new List<PcbLayerEntry>();
        var first = entries.Values.FirstOrDefault(e => e.PreviousIndex == 0 || !entries.ContainsKey(e.PreviousIndex));
        if (first != null)
        {
            var current = first;
            var visited = new HashSet<int>();
            while (current != null && visited.Add(current.Index))
            {
                ordered.Add(current);
                entries.TryGetValue(current.NextIndex, out current);
            }
        }

        // If chain traversal missed some entries, add them at the end
        if (ordered.Count < entries.Count)
        {
            foreach (var entry in entries.Values.OrderBy(e => e.Index))
            {
                if (!ordered.Contains(entry))
                    ordered.Add(entry);
            }
        }

        return new PcbLayerStack(ordered);
    }
}
