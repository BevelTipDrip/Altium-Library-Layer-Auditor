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
    public static List<List<CoordPoint>>? TryProjectTopDown(
        PcbComponentBody body,
        IReadOnlyList<PcbModel> models,
        ProjectionCache? cache = null)
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

        double extent = Math.Max(maxX - minX, maxY - minY);
        if (extent <= 0) return null;
        // Cell size scales with the part so large and tiny bodies both get a reasonable cell count
        // (roughly extent/300), clamped to a sane range — fine enough to look like the part, coarse
        // enough that even a large connector's grid stays small.
        double cellMm = Math.Clamp(extent / 300.0, 0.01, 0.05);

        var loopsMm = RasterizeAndTrace(projected, minX, minY, maxX, maxY, cellMm);
        if (loopsMm.Count == 0) return null;

        return loopsMm
            .Select(loop => loop.Select(p => new CoordPoint(Coord.FromMm(p.X), Coord.FromMm(p.Y))).ToList())
            .ToList();
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

    // Rasterizes the projected 2D triangles onto a uniform grid and traces the boundary of the
    // filled cells into closed loops — the same "grid + boundary edge" contour trace Program.cs's
    // BoxyUnionOutline uses for rectangles, generalized to a triangle fill test (point-in-triangle
    // via barycentric coordinates, sampled at each cell's center) so a non-rectilinear STEP shape
    // still produces a clean vector outline instead of a rough bounding box.
    private static List<List<(double X, double Y)>> RasterizeAndTrace(
        List<(double Ax, double Ay, double Bx, double By, double Cx, double Cy)> triangles,
        double minX, double minY, double maxX, double maxY, double cellMm)
    {
        int nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / cellMm));
        int ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / cellMm));
        var filled = new bool[nx, ny];

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
            // A real (even very slightly tilted) silhouette almost never lands exactly on grid lines,
            // so its edges rasterize as a one-cell staircase that plain collinear-merging can't touch
            // — every step is a genuine corner. Simplify with a tolerance instead (Douglas-Peucker, at
            // the grid's own cell size) so a near-straight edge collapses to the few segments it
            // visually is, while corners/notches bigger than one cell survive intact.
            if (raw.Count >= 3) loops.Add(SimplifyLoop(raw, cellMm));
        }
        return loops;
    }

    // Closed-loop Douglas-Peucker: split the loop at its farthest-apart pair of points into two open
    // chains, simplify each chain independently, then rejoin. (Standard DP needs two distinct
    // endpoints to measure perpendicular distance from; a closed loop has none on its own.)
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

        var s1 = DouglasPeucker(chain1, tolerance);
        var s2 = DouglasPeucker(chain2, tolerance);

        var result = new List<(double X, double Y)>(s1.Count + s2.Count - 2);
        result.AddRange(s1.Take(s1.Count - 1));
        result.AddRange(s2.Take(s2.Count - 1));
        return result.Count >= 3 ? result : pts;
    }

    private static List<(double X, double Y)> DouglasPeucker(List<(double X, double Y)> pts, double tolerance)
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
            var left = DouglasPeucker(pts.GetRange(0, index + 1), tolerance);
            var right = DouglasPeucker(pts.GetRange(index, pts.Count - index), tolerance);
            var combined = new List<(double, double)>(left.Count + right.Count - 1);
            combined.AddRange(left.Take(left.Count - 1));
            combined.AddRange(right);
            return combined;
        }

        return new List<(double, double)> { pts[0], pts[^1] };
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
