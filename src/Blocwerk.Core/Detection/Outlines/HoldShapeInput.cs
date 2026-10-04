using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>One hold on a panel, as the overlap resolver sees it (all in normalized photo coordinates).</summary>
/// <param name="Id">The hold id (the resolver's deterministic order).</param>
/// <param name="X">Centre X.</param>
/// <param name="Y">Centre Y.</param>
/// <param name="Radius">The circle radius (the rendered circle is a circle in normalized space).</param>
/// <param name="Shape">The outline as offsets from the centre, or null for the plain circle.</param>
/// <param name="Locked">
/// True for anything the resolver must never change: hand-drawn shapes, manually placed holds, plain circles it
/// has no business with. Locked holds are obstacles that always win.
/// </param>
public sealed record HoldShapeInput(Guid Id, double X, double Y, double Radius, IReadOnlyList<ShapePoint>? Shape, bool Locked);

/// <summary>How the resolver fitted one unlocked hold.</summary>
public enum HoldShapeFit
{
    /// <summary>It overlapped nothing; shape and radius are as given.</summary>
    Unchanged = 0,

    /// <summary>The outline was pulled in (clipped / shrunk toward the centre) until it cleared its neighbours.</summary>
    Shrunk = 1,

    /// <summary>The outline could not be fitted, so the hold is the plain circle at its original radius.</summary>
    Circle = 2,

    /// <summary>The circle overlapped too, so the radius was reduced (not below the minimum).</summary>
    ShrunkCircle = 3,

    /// <summary>Even the floor-size circle overlaps (e.g. its centre lies inside a locked hold); the original circle is kept.</summary>
    Unresolved = 4,
}

/// <summary>The resolver's decision for one unlocked hold.</summary>
/// <param name="Id">The hold.</param>
/// <param name="Fit">What was done.</param>
/// <param name="Shape">The final outline offsets, or null for the plain circle.</param>
/// <param name="Radius">The final radius (equal to the input radius unless <see cref="HoldShapeFit.ShrunkCircle"/>).</param>
public sealed record HoldShapeResolution(Guid Id, HoldShapeFit Fit, IReadOnlyList<ShapePoint>? Shape, double Radius);
