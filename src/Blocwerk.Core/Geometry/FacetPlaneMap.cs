namespace Blocwerk.Core.Geometry;

/// <summary>How well a plane mapping is constrained.</summary>
public enum PlaneMappingMode
{
    /// <summary>
    /// Exact 4-point fit to one marker: exact at the marker, degrading with distance from it
    /// (a marker's corner error is amplified by distance / marker size).
    /// </summary>
    SingleMarker,

    /// <summary>Robust (RANSAC) fit over the corners of two or more markers of the same facet.</summary>
    MultiMarker,
}

/// <summary>
/// One photo's pixel ↔ plane-mm mapping for one facet (or, as a fallback, for one marker's own
/// local frame). Plane coordinates follow the facet frame: <c>a</c> across (right), <c>b</c> up.
/// </summary>
public sealed class FacetPlaneMap
{
    private readonly PlaneHomography planeToImage;
    private readonly PlaneHomography imageToPlane;
    private readonly double visibleDepthSign;

    internal FacetPlaneMap(PlaneHomography toImage, PlaneHomography toPlane, FacetPlaneMapInfo info)
    {
        planeToImage = toImage;
        imageToPlane = toPlane;

        // The image→plane depth changes sign across the plane's horizon; the side the markers
        // were seen on is the visible one.
        visibleDepthSign = Math.Sign(imageToPlane.Depth(info.ReferencePx.X, info.ReferencePx.Y));
        SegmentIndex = info.SegmentIndex;
        FacetId = info.FacetId;
        IsLocalFrame = info.IsLocalFrame;
        MarkerIds = info.MarkerIds;
        Mode = info.MarkerIds.Count >= 2 ? PlaneMappingMode.MultiMarker : PlaneMappingMode.SingleMarker;
        CornerReprojRmsPx = info.CornerReprojRmsPx;
        InlierCornerCount = info.InlierCornerCount;
        TotalCornerCount = info.TotalCornerCount;
    }

    /// <summary>Segment index (from the wall's marker layout; -1 for an id the plan does not list).</summary>
    public int SegmentIndex { get; }

    /// <summary>The facet id from the geometry document; null for a local marker frame.</summary>
    public string? FacetId { get; }

    /// <summary>
    /// True when this is NOT a wall facet frame but one marker's own square (origin at its BL
    /// corner, TL at (0, size)). Only local distances/scale are meaningful; positions are not
    /// comparable across markers or photos.
    /// </summary>
    public bool IsLocalFrame { get; }

    /// <summary>Markers whose corners support the fit (inliers only for multi-marker fits).</summary>
    public IReadOnlyList<int> MarkerIds { get; }

    public int MarkerCount => MarkerIds.Count;

    public PlaneMappingMode Mode { get; }

    /// <summary>RMS image-space distance between detected corners and the reprojected plane corners (inliers).</summary>
    public double CornerReprojRmsPx { get; }

    public int InlierCornerCount { get; }

    public int TotalCornerCount { get; }

    /// <summary>The underlying plane-mm → image-px homography.</summary>
    public PlaneHomography PlaneToImage => planeToImage;

    /// <summary>The underlying image-px → plane-mm homography (no horizon check; see <see cref="ImageToPlaneMm"/>).</summary>
    public PlaneHomography ImageToPlane => imageToPlane;

    /// <summary>Maps an image pixel to plane mm; NaN when the pixel lies beyond the plane's horizon.</summary>
    public (double A, double B) ImageToPlaneMm(double x, double y)
    {
        if (visibleDepthSign * imageToPlane.Depth(x, y) <= 0)
        {
            return (double.NaN, double.NaN);
        }

        return imageToPlane.Apply(x, y);
    }

    /// <summary>Maps plane mm to an image pixel (which may lie outside the image).</summary>
    public (double X, double Y) PlaneMmToImage(double a, double b) => planeToImage.Apply(a, b);

    /// <summary>
    /// Local scale in mm per pixel at an image point: sqrt(|det J|) of the image→plane map, i.e. the
    /// geometric mean over directions. Varies across the image under perspective.
    /// </summary>
    public double LocalMmPerPx(double x, double y)
    {
        var j = imageToPlane.Jacobian(x, y);
        return Math.Sqrt(Math.Abs((j.Dxx * j.Dyy) - (j.Dxy * j.Dyx)));
    }
}
