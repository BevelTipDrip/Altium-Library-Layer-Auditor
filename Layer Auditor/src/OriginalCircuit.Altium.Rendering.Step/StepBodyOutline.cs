using System.Threading.Tasks;
using OriginalCircuit.Altium.Models.Pcb;
using OriginalCircuit.Eda.Primitives;
using OriginalCircuit.Mech.STEP.Geometry;
using OriginalCircuit.Mech.STEP.Schema;
using OriginalCircuit.Mech.STEP.Tessellation;
using OriginalCircuit.Mech.STEP.Topology;

namespace OriginalCircuit.Altium.Rendering.Step;

/// <summary>
/// Computes the true top-down silhouette of a placed <see cref="PcbComponentBody"/>'s embedded STEP
/// model — an accurate replacement for the rough 2D <see cref="PcbComponentBody.Outline"/> Altium
/// stores alongside the body, for courtyard/assembly outline generation.
/// </summary>
public static class StepBodyOutline
{
    /// <summary>
    /// Projects <paramref name="body"/>'s STEP model straight down (viewed from +Z) and traces its
    /// outer silhouette, in the footprint's own <see cref="CoordPoint"/> space (the same space as
    /// <see cref="PcbComponentBody.Outline"/> and every other primitive on the footprint). Returns
    /// <see langword="null"/> when there is no usable STEP model to project — the body has no
    /// <see cref="PcbComponentBody.ModelId"/>, no matching entry in <paramref name="models"/>, an
    /// empty <see cref="PcbModel.StepData"/>, or the payload could not be parsed/tessellated — in
    /// which case the caller should fall back to the stored <see cref="PcbComponentBody.Outline"/>.
    /// </summary>
    /// <param name="body">The placed component body to project.</param>
    /// <param name="models">The library's model table (<c>PcbLibrary.Models</c>) that <see cref="PcbComponentBody.ModelId"/> indexes into.</param>
    /// <param name="cache">
    /// Optional cache (see <see cref="CreateCache"/>) of tessellated-and-flattened local-space
    /// triangles, keyed internally by <see cref="PcbModel.Id"/>. Reuses the (comparatively
    /// expensive) STEP parse/tessellate step across multiple bodies that share the same model within
    /// one caller — e.g. several instances of the same connector on one footprint. Pass the same
    /// instance across calls to benefit; omit for a one-off projection.
    /// </param>
    /// <param name="smoothingMm">
    /// Corner-simplification tolerance (in mm) applied to the traced outline — larger values collapse
    /// a run of small staircase/fillet/notch detail into a single right-angle elbow between its two
    /// dominant corners (see <see cref="SimplifyLoop"/>) in favour of a looser, more general "boxy"
    /// shape with angular, not curve-following, corners; 0 keeps detail down to the rasterization
    /// grid's own resolution. Because the elbow chosen at each step is provably at least as large as
    /// any staircase it could be replacing, simplification only ever adds area, never removes any —
    /// the traced outline never cuts closer to the true silhouette than the un-smoothed shape already
    /// was — and every edge, including the elbow itself, stays strictly horizontal/vertical at any
    /// smoothing level, never a diagonal cut corner.
    /// </param>
    public static List<List<CoordPoint>>? TryProjectTopDown(
        PcbComponentBody body,
        IReadOnlyList<PcbModel> models,
        ProjectionCache? cache,
        double smoothingMm)
    {
        var placed = GetPlacedTriangles(body, models, cache);
        if (placed is not { } p) return null;

        double extent = Math.Max(p.MaxX - p.MinX, p.MaxY - p.MinY);
        if (extent <= 0) return null;
        double cellMm = GridCellMm(extent);

        int nx = CellCount(p.MaxX - p.MinX, cellMm);
        int ny = CellCount(p.MaxY - p.MinY, cellMm);
        var filled = new bool[nx, ny];
        RasterizeTrianglesInto(filled, p.Triangles, p.MinX, p.MinY, cellMm, nx, ny);

        var loopsMm = TraceGridBoundary(filled, nx, ny, p.MinX, p.MinY, cellMm, Math.Max(cellMm, smoothingMm));
        return loopsMm.Count == 0 ? null : ToCoordLoops(loopsMm);
    }

    /// <summary>
    /// Builds a courtyard outline for an entire footprint in one pass: each body with a usable STEP
    /// model contributes its true top-down silhouette expanded by <paramref name="bodyOffsetMm"/>
    /// (a real geometric offset — via distance-transform dilation, not just a bigger bounding box);
    /// each body without one falls back to its stored <see cref="PcbComponentBody.Bounds"/> rectangle
    /// expanded by the same offset; <paramref name="padRectsOffset"/> (already expanded by the
    /// caller's own pad offset) is unioned in directly. Returns <see langword="null"/> when no body
    /// has a usable STEP model at all — the caller should fall back to its own exact rectangle union
    /// (e.g. <c>BoxyUnionOutline</c>) in that case, since a pure-rectangle case doesn't need (and
    /// loses precision from) this method's rasterized grid.
    /// </summary>
    /// <param name="bodies">Every body on the footprint with a non-empty <see cref="PcbComponentBody.Bounds"/>.</param>
    /// <param name="models">The library's model table (<c>PcbLibrary.Models</c>).</param>
    /// <param name="bodyOffsetMm">
    /// How far the courtyard must clear each body's true silhouette, in mm — a floor, not a target:
    /// the dilation threshold is deliberately biased outward by one grid cell (see
    /// <see cref="OffsetSafetyMarginCells"/>) so quantization error can only make the drawn boundary
    /// sit a little farther from the body than requested, never closer, and the boundary never
    /// crosses the body's own outline.
    /// </param>
    /// <param name="padRectsOffset">Each pad's bounds, already expanded by the caller's pad offset, in the footprint's own <see cref="CoordPoint"/> space.</param>
    /// <param name="smoothingMm">Corner-simplification tolerance (in mm) applied to the final traced outline — see <see cref="TryProjectTopDown"/>; also only ever adds clearance, never removes it.</param>
    /// <param name="cache">Optional cache — see <see cref="TryProjectTopDown"/>.</param>
    public static (List<List<CoordPoint>>? Loops, int ProjectedBodyCount) TryProjectCourtyardOutline(
        IReadOnlyList<PcbComponentBody> bodies,
        IReadOnlyList<PcbModel> models,
        double bodyOffsetMm,
        IReadOnlyList<CoordRect> padRectsOffset,
        double smoothingMm,
        ProjectionCache? cache)
    {
        var stepTriangleSets = new List<List<(double Ax, double Ay, double Bx, double By, double Cx, double Cy)>>();
        var rectsMm = new List<(double MinX, double MinY, double MaxX, double MaxY)>();
        int projectedCount = 0;

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Expand(double x0, double y0, double x1, double y1)
        {
            minX = Math.Min(minX, x0); minY = Math.Min(minY, y0);
            maxX = Math.Max(maxX, x1); maxY = Math.Max(maxY, y1);
        }

        foreach (var b in bodies)
        {
            var placed = GetPlacedTriangles(b, models, cache);
            if (placed is { } p)
            {
                stepTriangleSets.Add(p.Triangles.Select(t => (t.Ax, t.Ay, t.Bx, t.By, t.Cx, t.Cy)).ToList());
                projectedCount++;
                Expand(p.MinX - bodyOffsetMm, p.MinY - bodyOffsetMm, p.MaxX + bodyOffsetMm, p.MaxY + bodyOffsetMm);
            }
            else if (!b.Bounds.IsEmpty)
            {
                var r = b.Bounds.Inflate(Coord.FromMm(bodyOffsetMm));
                rectsMm.Add((r.Min.X.ToMm(), r.Min.Y.ToMm(), r.Max.X.ToMm(), r.Max.Y.ToMm()));
                Expand(r.Min.X.ToMm(), r.Min.Y.ToMm(), r.Max.X.ToMm(), r.Max.Y.ToMm());
            }
        }

        // A footprint with no STEP-projectable body at all is a pure rectangle-union case: the caller
        // already has an exact (non-rasterized) way to do that, so hand it back rather than losing
        // precision by routing plain rectangles through this grid too.
        if (stepTriangleSets.Count == 0) return (null, 0);

        var padRectsMm = padRectsOffset.Where(r => !r.IsEmpty)
            .Select(r => (r.Min.X.ToMm(), r.Min.Y.ToMm(), r.Max.X.ToMm(), r.Max.Y.ToMm())).ToList();
        foreach (var (x0, y0, x1, y1) in padRectsMm) Expand(x0, y0, x1, y1);

        if (!double.IsFinite(minX) || !double.IsFinite(maxX)) return (null, projectedCount);
        double extent = Math.Max(maxX - minX, maxY - minY);
        if (extent <= 0) return (null, projectedCount);
        double cellMm = GridCellMm(extent);
        int nx = CellCount(maxX - minX, cellMm);
        int ny = CellCount(maxY - minY, cellMm);

        // Raw (un-offset) fill of every STEP body's triangles, unioned into one grid — dilation
        // distributes over union, so running the distance transform once here is exactly equivalent
        // to dilating each body separately by bodyOffsetMm and then unioning the results.
        var rawBodyFilled = new bool[nx, ny];
        foreach (var tris in stepTriangleSets) RasterizeTrianglesInto(rawBodyFilled, tris, minX, minY, cellMm, nx, ny);

        var filled = new bool[nx, ny];
        var edtSq = SquaredEdt(rawBodyFilled, nx, ny);
        // Biased outward by a full cell: cell-center sampling in both the raw rasterization and this
        // threshold test can each misjudge a boundary cell by a fraction of a cell in either
        // direction, so a bare offsetCells threshold could under-clear the true body edge by up to
        // roughly a cell. The margin trades a little (sub-0.05mm at the default grid) extra, harmless
        // clearance for a hard guarantee that the drawn boundary is never closer than bodyOffsetMm.
        double offsetCells = (bodyOffsetMm / cellMm) + OffsetSafetyMarginCells;
        double offsetCellsSq = offsetCells * offsetCells;
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
            if (edtSq[i, j] <= offsetCellsSq) filled[i, j] = true;

        // Non-STEP body rects and pad rects are already at their final (offset) size — fill directly,
        // no dilation needed (FillRectInto's cell-containment rule already rounds outward, never in).
        foreach (var r in rectsMm) FillRectInto(filled, r, minX, minY, cellMm, nx, ny);
        foreach (var r in padRectsMm) FillRectInto(filled, r, minX, minY, cellMm, nx, ny);

        var loopsMm = TraceGridBoundary(filled, nx, ny, minX, minY, cellMm, Math.Max(cellMm, smoothingMm));
        return loopsMm.Count == 0 ? (null, projectedCount) : (ToCoordLoops(loopsMm), projectedCount);
    }

    // Safety margin (in grid cells) added to every distance-based offset threshold so quantization
    // error can only push the drawn boundary a little farther from the body than requested, never
    // closer — see TryProjectCourtyardOutline's bodyOffsetMm remarks.
    private const double OffsetSafetyMarginCells = 1.0;

    // Cell size scales with the part so large and tiny bodies both get a reasonable cell count
    // (roughly extent/300), clamped to a sane range — fine enough to look like the part, coarse
    // enough that even a large connector's grid stays small.
    private static double GridCellMm(double extentMm) => Math.Clamp(extentMm / 300.0, 0.01, 0.05);

    private static int CellCount(double spanMm, double cellMm) => Math.Max(1, (int)Math.Ceiling(spanMm / cellMm));

    private static List<List<CoordPoint>> ToCoordLoops(List<List<(double X, double Y)>> loopsMm) =>
        loopsMm.Select(loop => loop.Select(p => new CoordPoint(Coord.FromMm(p.X), Coord.FromMm(p.Y))).ToList()).ToList();

    // Placed (world/footprint-space, in mm) triangles for one body's STEP model, plus their bounds.
    // Returns null when there is no usable STEP model — no ModelId, no matching PcbModel, empty
    // StepData, or the payload could not be parsed/tessellated within the time budget.
    private static (List<(double Ax, double Ay, double Bx, double By, double Cx, double Cy)> Triangles,
        double MinX, double MinY, double MaxX, double MaxY)? GetPlacedTriangles(
        PcbComponentBody body, IReadOnlyList<PcbModel> models, ProjectionCache? cache)
    {
        if (string.IsNullOrEmpty(body.ModelId)) return null;

        var triangles = GetLocalTriangles(body.ModelId, models, cache);
        if (triangles is null || triangles.Count == 0) return null;

        // Body placement: rotate by the model's absolute orientation (Model3DRotX/Y/Z already
        // includes any axis correction baked in by Altium — no PcbModel.RotationX/Y/Z should be
        // applied on top, or the model gets double-rotated) plus the footprint's own in-plane
        // rotation, then translate to the footprint 2D location. Mirrors the placement chain in
        // AltiumSharp's GltfComponentPlacer.EmitBody, minus the Z terms (standoff/board stack),
        // which only affect height and are irrelevant once Z is dropped for a top-down silhouette —
        // and minus any bottom-side mirroring, which that placer also does not apply to X/Y (bottom
        // vs. top only changes which way Z is mirrored there, never the in-plane placement).
        double rx = body.Model3DRotX, ry = body.Model3DRotY, rz = body.Model3DRotZ + body.Model2DRotation;
        double txMm = body.Model2DLocation.X.ToMm();
        double tyMm = body.Model2DLocation.Y.ToMm();

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var projected = new List<(double Ax, double Ay, double Bx, double By, double Cx, double Cy)>(triangles.Count);
        foreach (var (a, b, c) in triangles)
        {
            var (ax, ay) = PlaceXY(a, rx, ry, rz, txMm, tyMm);
            var (bx, by) = PlaceXY(b, rx, ry, rz, txMm, tyMm);
            var (cx, cy) = PlaceXY(c, rx, ry, rz, txMm, tyMm);
            projected.Add((ax, ay, bx, by, cx, cy));
            minX = Math.Min(minX, Math.Min(ax, Math.Min(bx, cx)));
            minY = Math.Min(minY, Math.Min(ay, Math.Min(by, cy)));
            maxX = Math.Max(maxX, Math.Max(ax, Math.Max(bx, cx)));
            maxY = Math.Max(maxY, Math.Max(ay, Math.Max(by, cy)));
        }
        if (projected.Count == 0 || !double.IsFinite(minX) || !double.IsFinite(maxX)) return null;

        return (projected, minX, minY, maxX, maxY);
    }

    private static (double X, double Y) PlaceXY(Vec3 p, double degX, double degY, double degZ, double txMm, double tyMm)
    {
        double x = p.X, y = p.Y, z = p.Z;
        if (degX != 0) { double a = degX * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a); (y, z) = ((y * c) - (z * s), (y * s) + (z * c)); }
        if (degY != 0) { double a = degY * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a); (x, z) = ((x * c) + (z * s), (-x * s) + (z * c)); }
        if (degZ != 0) { double a = degZ * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a); (x, y) = ((x * c) - (y * s), (x * s) + (y * c)); }
        return (x + txMm, y + tyMm);
    }

    // Parses and tessellates the model (once per model id, cached across bodies when a cache is
    // supplied) into triangles in the model's own local/canonical frame — no body placement applied
    // yet. Returns null if the model can't be resolved, has no STEP payload, or fails to
    // parse/tessellate (an unsupported STEP feature, malformed data, etc. — never throws).
    // A model with hundreds of individually-placed sub-parts (e.g. a large BGA's solder balls, each
    // its own assembly occurrence) has been observed to take Mech.STEP's tessellator minutes rather
    // than seconds — independent of chord tolerance, so it isn't curve-segment fidelity driving it.
    // Rather than let one pathological model hang a "Generate" click indefinitely, tessellation gets
    // a hard wall-clock budget; past it we abandon the attempt (the background work keeps running to
    // completion but its result is discarded) and the caller falls back to the stored Outline, same
    // as any other unprojectable model.
    private static readonly TimeSpan TessellationBudget = TimeSpan.FromSeconds(8);

    private static IReadOnlyList<(Vec3 A, Vec3 B, Vec3 C)>? GetLocalTriangles(
        string modelId, IReadOnlyList<PcbModel> models, ProjectionCache? cache)
    {
        if (cache is not null && cache.Triangles.TryGetValue(modelId, out var cached)) return cached;

        IReadOnlyList<(Vec3, Vec3, Vec3)>? result = null;
        var model = models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));
        if (model is not null && !string.IsNullOrWhiteSpace(model.StepData))
        {
            var task = Task.Run(() => TessellateToTriangles(model.StepData));
            result = task.Wait(TessellationBudget) ? task.Result : null;
        }

        if (cache is not null) cache.Triangles[modelId] = result;
        return result;
    }

    // Parses and tessellates one model's STEP payload into local-frame triangles. Never throws — a
    // model that cannot be parsed/tessellated (an unsupported STEP feature, malformed data, ...) is
    // skipped so the caller falls back to the stored Outline rather than failing generation outright.
    private static List<(Vec3, Vec3, Vec3)>? TessellateToTriangles(string stepData)
    {
        try
        {
            var stepModel = StepModel.Parse(stepData);
            var tess = new Tessellator(stepModel, TessellationOptions.Default);

            var triangles = new List<(Vec3, Vec3, Vec3)>();
            foreach (var (transform, mesh) in CollectMeshes(tess, stepModel))
            {
                for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
                {
                    var a = transform.TransformPoint(mesh.Positions[mesh.Indices[t]]);
                    var b = transform.TransformPoint(mesh.Positions[mesh.Indices[t + 1]]);
                    var c = transform.TransformPoint(mesh.Positions[mesh.Indices[t + 2]]);
                    triangles.Add((a, b, c));
                }
            }
            return triangles.Count > 0 ? triangles : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// An opaque per-call cache for <see cref="TryProjectTopDown"/> — pass the same instance across
    /// multiple bodies (e.g. every body on one footprint) to tessellate each distinct STEP model at
    /// most once. Not thread-safe; create one per caller/request.
    /// </summary>
    public sealed class ProjectionCache
    {
        internal readonly Dictionary<string, IReadOnlyList<(Vec3, Vec3, Vec3)>?> Triangles = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates a new, empty <see cref="ProjectionCache"/>.</summary>
    public static ProjectionCache CreateCache() => new();

    // Same two-tier strategy as GltfComponentPlacer.CollectMeshes: prefer the assembly scene (one
    // mesh per placed occurrence, correctly positioned relative to each other); fall back to
    // tessellating every representation directly for a model with no assembly occurrences at all.
    private static List<(Matrix4 Transform, TriangleMesh Mesh)> CollectMeshes(Tessellator tess, StepModel model)
    {
        var found = new List<(Matrix4, TriangleMesh)>();
        foreach (var item in Flatten(tess.TessellateScene(), Matrix4.Identity)) found.Add(item);
        if (found.Count > 0) return found;

        foreach (var rep in model.OfType<Representation>())
        {
            var mesh = tess.TessellateRepresentation(rep);
            if (mesh is not null) found.Add((Matrix4.Identity, mesh));
        }
        return found;
    }

    private static IEnumerable<(Matrix4 Transform, TriangleMesh Mesh)> Flatten(SceneNode node, Matrix4 parent)
    {
        Matrix4 world = parent * node.Transform;
        if (node.Mesh is { } mesh) yield return (world, mesh);
        foreach (var child in node.Children)
            foreach (var item in Flatten(child, world))
                yield return item;
    }

    // Rasterizes a set of 2D triangles (mm, footprint space) onto the given grid — a cell is filled
    // when its center falls inside any triangle. Additive: only ever turns cells on, so multiple
    // calls into the same grid (e.g. one per body) union correctly.
    private static void RasterizeTrianglesInto(
        bool[,] filled, List<(double Ax, double Ay, double Bx, double By, double Cx, double Cy)> triangles,
        double minX, double minY, double cellMm, int nx, int ny)
    {
        foreach (var (ax, ay, bx, by, cx, cy) in triangles)
        {
            int i0 = Math.Max(0, (int)((Math.Min(ax, Math.Min(bx, cx)) - minX) / cellMm));
            int i1 = Math.Min(nx - 1, (int)((Math.Max(ax, Math.Max(bx, cx)) - minX) / cellMm));
            int j0 = Math.Max(0, (int)((Math.Min(ay, Math.Min(by, cy)) - minY) / cellMm));
            int j1 = Math.Min(ny - 1, (int)((Math.Max(ay, Math.Max(by, cy)) - minY) / cellMm));
            for (int i = i0; i <= i1; i++)
            {
                double px = minX + (i + 0.5) * cellMm;
                for (int j = j0; j <= j1; j++)
                {
                    if (filled[i, j]) continue;
                    double py = minY + (j + 0.5) * cellMm;
                    if (PointInTriangle(px, py, ax, ay, bx, by, cx, cy)) filled[i, j] = true;
                }
            }
        }
    }

    // Rasterizes one already-final-size axis-aligned rectangle (mm, footprint space) onto the grid —
    // trivial compared to triangles since a rect's cell range IS its fill, no per-cell test needed.
    private static void FillRectInto(bool[,] filled, (double MinX, double MinY, double MaxX, double MaxY) rect, double minX, double minY, double cellMm, int nx, int ny)
    {
        int i0 = Math.Max(0, (int)((rect.MinX - minX) / cellMm));
        int i1 = Math.Min(nx - 1, (int)((rect.MaxX - minX) / cellMm));
        int j0 = Math.Max(0, (int)((rect.MinY - minY) / cellMm));
        int j1 = Math.Min(ny - 1, (int)((rect.MaxY - minY) / cellMm));
        for (int i = i0; i <= i1; i++)
        for (int j = j0; j <= j1; j++)
            filled[i, j] = true;
    }

    // 2D squared Euclidean distance transform (Felzenszwalt & Huttenlocher's lower-envelope-of-
    // parabolas method: a 1D transform along each column, then along each row of the result — the
    // standard exact-and-separable construction, not an approximation). Used to dilate a rasterized
    // shape by an arbitrary real-world offset without the O(filled-cells x disc-area) cost of naively
    // stamping a disc around every filled cell. Distances are in cell units (squared).
    private static double[,] SquaredEdt(bool[,] filled, int nx, int ny)
    {
        const double Inf = 1e18;
        var g = new double[nx, ny];
        var col = new double[ny];
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < ny; j++) col[j] = filled[i, j] ? 0 : Inf;
            var d = Dt1D(col);
            for (int j = 0; j < ny; j++) g[i, j] = d[j];
        }

        var outp = new double[nx, ny];
        var row = new double[nx];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++) row[i] = g[i, j];
            var d = Dt1D(row);
            for (int i = 0; i < nx; i++) outp[i, j] = d[i];
        }
        return outp;
    }

    // 1D squared distance transform via the lower envelope of parabolas rooted at each sample. f[q]
    // holds 0 at a "seed" and a large sentinel elsewhere; returns, for every q, the minimum over all
    // seeds p of (q-p)^2 + f[p].
    private static double[] Dt1D(double[] f)
    {
        int n = f.Length;
        var d = new double[n];
        var v = new int[n];
        var z = new double[n + 1];
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;
        for (int q = 1; q < n; q++)
        {
            double s = Intersect(f, q, v[k]);
            while (s <= z[k])
            {
                k--;
                s = Intersect(f, q, v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k];
            d[q] = (dq * dq) + f[v[k]];
        }
        return d;
    }

    private static double Intersect(double[] f, int q, int v) =>
        ((f[q] + ((double)q * q)) - (f[v] + ((double)v * v))) / ((2.0 * q) - (2.0 * v));

    // Traces the boundary of the filled cells into closed loops — the same "grid + boundary edge"
    // contour trace Program.cs's BoxyUnionOutline uses for rectangles, generalized to a triangle fill
    // test — then simplifies each loop down to its dominant corners.
    private static List<List<(double X, double Y)>> TraceGridBoundary(
        bool[,] filled, int nx, int ny, double minX, double minY, double cellMm, double simplifyTolerance)
    {
        var xs = new double[nx + 1];
        for (int i = 0; i <= nx; i++) xs[i] = minX + i * cellMm;
        var ys = new double[ny + 1];
        for (int j = 0; j <= ny; j++) ys[j] = minY + j * cellMm;

        // Each boundary edge is emitted walking counter-clockwise around its filled cell, so every
        // vertex ends up with exactly one outgoing edge (the invariant the loop-walk below relies on)
        // — identical scheme to BoxyUnionOutline.
        var outgoing = new Dictionary<(double, double), (double, double)>();
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
        {
            if (!filled[i, j]) continue;
            if (i == 0 || !filled[i - 1, j]) outgoing[(xs[i], ys[j])] = (xs[i], ys[j + 1]);
            if (i == nx - 1 || !filled[i + 1, j]) outgoing[(xs[i + 1], ys[j + 1])] = (xs[i + 1], ys[j]);
            if (j == 0 || !filled[i, j - 1]) outgoing[(xs[i + 1], ys[j])] = (xs[i], ys[j]);
            if (j == ny - 1 || !filled[i, j + 1]) outgoing[(xs[i], ys[j + 1])] = (xs[i + 1], ys[j + 1]);
        }

        var visited = new HashSet<(double, double)>();
        var loops = new List<List<(double X, double Y)>>();
        foreach (var start in outgoing.Keys)
        {
            if (visited.Contains(start)) continue;
            var raw = new List<(double, double)> { start };
            var cur = start;
            while (true)
            {
                visited.Add(cur);
                var next = outgoing[cur];
                if (next == start) break;
                raw.Add(next);
                cur = next;
            }
            if (raw.Count >= 3) loops.Add(SimplifyLoop(raw, simplifyTolerance));
        }
        return loops;
    }

    // Closed-loop rectilinear corner simplification: recursively finds each run's two most-distant
    // points, splits there, and (see RectilinearDP) collapses any sub-run within tolerance of its own
    // chord to a single right-angle elbow between its endpoints — real Altium assembly/courtyard
    // outlines cut a rounded/filleted transition to one sharp step rather than tracing it, and this
    // matches that convention instead of following every bit of curvature down to grid resolution.
    // (Closed-loop framing mirrors the old plain-collinear merge this replaced: split into two open
    // chains at the farthest-apart pair of points, simplify each independently, then rejoin — standard
    // DP needs two distinct endpoints to measure a chord from, and a closed loop has none on its own.)
    private static List<(double X, double Y)> SimplifyLoop(List<(double X, double Y)> pts, double tolerance)
    {
        if (pts.Count <= 4) return pts;

        int ia = 0, ib = 1;
        double best = -1;
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                double d = (dx * dx) + (dy * dy);
                if (d > best) { best = d; ia = i; ib = j; }
            }

        var chain1 = pts.GetRange(ia, ib - ia + 1);
        var chain2 = new List<(double X, double Y)>();
        for (int k = ib; k < pts.Count; k++) chain2.Add(pts[k]);
        for (int k = 0; k <= ia; k++) chain2.Add(pts[k]);

        var s1 = RectilinearDp(chain1, tolerance);
        var s2 = RectilinearDp(chain2, tolerance);

        var result = new List<(double X, double Y)>(s1.Count + s2.Count - 2);
        result.AddRange(s1.Take(s1.Count - 1));
        result.AddRange(s2.Take(s2.Count - 1));
        return result.Count >= 3 ? result : pts;
    }

    // Standard Douglas-Peucker point selection (find the point farthest from the S-E chord; if it's
    // within tolerance, the whole run collapses to just S and E; otherwise split there and recurse) —
    // but the collapse case never draws the S-E chord itself, since for two points that don't already
    // share an X or Y that chord would be a diagonal cutting across an otherwise axis-aligned shape.
    // Instead it draws a right-angle elbow through whichever of the two possible corners —
    // (S.X, E.Y) or (E.X, S.Y) — encloses more area (compared via the shoelace/signed-area formula on
    // the 3-point S/corner/E path). One of those two corners is always the "outer" one: for ANY
    // monotonic staircase between S and E (which a sub-run this small effectively always is, since a
    // genuine direction reversal would have produced enough chord distance to force a split before
    // reaching this base case), the outer corner's elbow fully contains it, and the inner corner's
    // elbow would cut into it. Picking the larger-area candidate always picks the outer one, so the
    // simplified run is guaranteed to enclose everything the original run did — never less.
    private static List<(double X, double Y)> RectilinearDp(List<(double X, double Y)> pts, double tolerance)
    {
        if (pts.Count < 3) return pts;

        double dmax = 0;
        int index = 0;
        var (ax, ay) = pts[0];
        var (bx, by) = pts[^1];
        for (int i = 1; i < pts.Count - 1; i++)
        {
            double d = PerpendicularDistance(pts[i], ax, ay, bx, by);
            if (d > dmax) { dmax = d; index = i; }
        }

        if (dmax > tolerance)
        {
            var left = RectilinearDp(pts.GetRange(0, index + 1), tolerance);
            var right = RectilinearDp(pts.GetRange(index, pts.Count - index), tolerance);
            var combined = new List<(double, double)>(left.Count + right.Count - 1);
            combined.AddRange(left.Take(left.Count - 1));
            combined.AddRange(right);
            return combined;
        }

        var s = pts[0];
        var e = pts[^1];
        if (s.X == e.X || s.Y == e.Y) return new List<(double, double)> { s, e };

        // The "outer corner always wins" argument assumes the run is a monotonic staircase between S
        // and E — true for an ordinary rounded/filleted transition, but not for a narrow tab/lead that
        // pokes out past the S-E chord and back again (still within `tolerance` of the chord itself,
        // since DP only measures perpendicular distance, not which side of the chord a point is on).
        // Neither elbow candidate can contain a shape like that, so before committing to one, check it
        // actually encloses at least as much as the real run did; if it doesn't (for either corner),
        // keep the original detail here rather than silently cutting into it.
        var cornerA = (X: s.X, Y: e.Y);
        var cornerB = (X: e.X, Y: s.Y);
        double areaOriginal = ShoelaceChain(pts);
        double areaA = ShoelacePartial(s, cornerA, e);
        double areaB = ShoelacePartial(s, cornerB, e);
        double bestArea = Math.Max(areaA, areaB);
        if (bestArea < areaOriginal - AreaSafetyEpsilon) return pts;

        var corner = areaA >= areaB ? cornerA : cornerB;
        return new List<(double, double)> { s, corner, e };
    }

    // Slack for the area-safety comparison so exact-zero-margin cases (a run that's already perfectly
    // rectilinear, where the "original" and "elbow" areas can come out equal up to floating-point
    // noise) aren't rejected as unsafe by a spurious sub-epsilon shortfall.
    private const double AreaSafetyEpsilon = 1e-9;

    private static double ShoelacePartial((double X, double Y) a, (double X, double Y) b, (double X, double Y) c) =>
        ((a.X * b.Y) - (b.X * a.Y)) + ((b.X * c.Y) - (c.X * b.Y));

    // Same (non-closing) partial shoelace sum as ShoelacePartial, but over the whole original run —
    // the baseline an elbow candidate must reach or exceed to be considered a safe replacement for it.
    private static double ShoelaceChain(List<(double X, double Y)> pts)
    {
        double sum = 0;
        for (int i = 0; i < pts.Count - 1; i++)
            sum += (pts[i].X * pts[i + 1].Y) - (pts[i + 1].X * pts[i].Y);
        return sum;
    }

    private static double PerpendicularDistance((double X, double Y) p, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-12) { double ex = p.X - ax, ey = p.Y - ay; return Math.Sqrt((ex * ex) + (ey * ey)); }
        return Math.Abs((dy * p.X) - (dx * p.Y) + (bx * ay) - (by * ax)) / len;
    }

    private static bool PointInTriangle(double px, double py, double ax, double ay, double bx, double by, double cx, double cy)
    {
        double d1 = Cross(px, py, ax, ay, bx, by);
        double d2 = Cross(px, py, bx, by, cx, cy);
        double d3 = Cross(px, py, cx, cy, ax, ay);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static double Cross(double px, double py, double ax, double ay, double bx, double by) =>
        ((bx - ax) * (py - ay)) - ((by - ay) * (px - ax));
}
