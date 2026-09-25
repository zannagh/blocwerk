// <copyright file="WallFrameRegistrationWriter.Rebase.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>
/// Claimed facets: the NEW solve's plane (already moved into the reference frame) with the reference facet's
/// id, and a plane frame rebased so that (a, b) keeps meaning (nearly) the same spot on the wall.
/// </summary>
public static partial class WallFrameRegistrationWriter
{
    /// <summary>The reference frame moved onto the solved plane (see <see cref="RebaseClaimedFrames"/>).</summary>
    internal static WallGeometryFacet Rebase(WallGeometryFacet old, WallGeometryFacet solved)
    {
        var n = solved.Normal!;
        var oldNormal = old.Normal is { Length: 3 } on ? Unit(on) : Unit(Cross(old.U!, old.V!));
        var u = InPlane(TurnOnto(old.U!, oldNormal, n), n);
        var v = Unit(Cross(n, u));
        if (Vec3.Dot(v, old.V!) < 0)
        {
            // Keep the reference's handedness (u × v = normal in the solver's convention, but never assume it).
            v = Vec3.Scale(v, -1);
        }

        var origin = Vec3.Sub(old.Origin!, Vec3.Scale(n, Vec3.Dot(Vec3.Sub(old.Origin!, solved.Origin!), n)));
        return new WallGeometryFacet { Id = old.Id, Origin = origin, U = u, V = v, Normal = n, ExtentMm = old.ExtentMm };
    }

    /// <summary>
    /// Every claimed facet takes the reference facet's id, but keeps its own solved plane (normal, position) and
    /// its own measured angles: a re-solve (a better gravity, more photos) must be able to change the geometry.
    /// Only its plane FRAME is rebased onto the reference one — origin = the reference origin projected onto the
    /// new plane, u/v = the reference axes turned by the smallest rotation from the old normal to the new — so
    /// a hold's (facet, a, b) still lands within millimetres of where it was. The extent covers both.
    /// </summary>
    /// <returns>Original (solved) facet id → its rebased frame, for re-projecting its markers.</returns>
    private static Dictionary<string, WallGeometryFacet> RebaseClaimedFrames(
        JsonObject root, WallGeometryDocument reference, Dictionary<string, string> claims, Dictionary<string, string> renamed)
    {
        var frames = new Dictionary<string, WallGeometryFacet>(StringComparer.Ordinal);
        foreach (var facet in Facets(root))
        {
            var id = facet["id"]!.GetValue<string>();
            facet["id"] = renamed[id];
            if (!claims.TryGetValue(id, out var target))
            {
                continue;
            }

            var old = reference.FindFacet(target)!.Value.Facet;
            var solved = FrameOf(facet);
            var frame = solved is null ? old : Rebase(old, solved);
            facet["origin"] = Array(frame.Origin!, 2);
            facet["u"] = Array(frame.U!, 6);
            facet["v"] = Array(frame.V!, 6);
            Set(facet, "normal", frame.Normal, 6);
            var extent = solved is null ? [] : Corners(solved).Select(x => ToPlane(frame, x));
            facet["extentMm"] = Extent(old.ExtentMm, extent, 0);
            frames[id] = frame;
        }

        return frames;
    }

    /// <summary>The solved facet's frame (already in the reference frame), or null when it has none.</summary>
    private static WallGeometryFacet? FrameOf(JsonObject facet)
    {
        if (Vec(facet["origin"]) is not { } origin || Vec(facet["u"]) is not { } u || Vec(facet["v"]) is not { } v)
        {
            return null;
        }

        var extent = facet["extentMm"] is JsonObject e
            ? new PlaneRectMm(Num(e, "aMin"), Num(e, "aMax"), Num(e, "bMin"), Num(e, "bMax"))
            : (PlaneRectMm?)null;
        return new WallGeometryFacet
        {
            Origin = origin,
            U = Unit(u),
            V = Unit(v),
            Normal = Unit(Vec(facet["normal"]) ?? Cross(u, v)),
            ExtentMm = extent,
        };
    }

    /// <summary>The smallest rotation taking unit <paramref name="from"/> onto unit <paramref name="to"/>, applied to <paramref name="x"/> (Rodrigues).</summary>
    private static double[] TurnOnto(double[] x, double[] from, double[] to)
    {
        var axis = Cross(from, to);
        var c = Vec3.Dot(from, to);
        if (c <= -0.999999)
        {
            return x;
        }

        // R·x = x + k×x + k×(k×x) / (1 + c), with k = from × to.
        var kx = Cross(axis, x);
        return Vec3.Add(Vec3.Add(x, kx), Vec3.Scale(Cross(axis, kx), 1 / (1 + c)));
    }

    private static double[] InPlane(double[] x, double[] n) => Unit(Vec3.Sub(x, Vec3.Scale(n, Vec3.Dot(x, n))));

    private static double[][] Corners(WallGeometryFacet f) => f.ExtentMm is not { } e
        ? []
        : [MarkerWorldCorners.ToWorld(f, e.AMin, e.BMin), MarkerWorldCorners.ToWorld(f, e.AMax, e.BMin), MarkerWorldCorners.ToWorld(f, e.AMax, e.BMax), MarkerWorldCorners.ToWorld(f, e.AMin, e.BMax)];

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double[] Unit(double[] a)
    {
        var length = Vec3.Length(a);
        return length < 1e-12 ? a : Vec3.Scale(a, 1 / length);
    }

    private static double Num(JsonObject node, string key) => node[key]?.GetValue<double>() ?? 0;

    /// <summary>
    /// The reference's <c>world</c> block, but with the NEW solve's up direction (turned into the reference frame)
    /// and whether it knew gravity: the angles are the new solve's, measured against that up.
    /// </summary>
    private static JsonNode? World(JsonObject root, JsonObject referenceRoot, RigidTransform3D transform)
    {
        var world = referenceRoot["world"]?.DeepClone() as JsonObject;
        if (world is null || root["world"] is not JsonObject solved)
        {
            return world;
        }

        world["up"] = Array(Unit(transform.Rotate(Vec(solved["up"]) ?? [0, 0, 1])), 6);
        if (solved["gravityKnown"] is { } known)
        {
            world["gravityKnown"] = known.DeepClone();
        }

        return world;
    }
}
