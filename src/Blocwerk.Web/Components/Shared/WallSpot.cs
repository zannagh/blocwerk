// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// A spot on the 3D wall: its facet and its point in the facet's frame (mm along the facet's axes, <paramref name="H"/>
/// above its plane). The "Show on wall" links of the volume and proposal lists carry one in <c>?at=</c>; the 3D view marks it.
/// </summary>
/// <param name="FacetId">The facet the spot is on.</param>
/// <param name="A">Mm along the facet's first axis.</param>
/// <param name="B">Mm along its second axis.</param>
/// <param name="H">Mm above the facet's plane (a volume's height).</param>
/// <param name="Look">
/// How the 3D view frames the spot: null looks straight at it; <see cref="Under"/> looks up at its underside from in
/// front of and below it (the hybrid zoom's "Look underneath" on a volume). Carried as a fifth <c>?at=</c> part.
/// </param>
public sealed record WallSpot(string FacetId, double A, double B, double H, string? Look = null)
{
    /// <summary>The <see cref="Look"/> that frames a volume from below.</summary>
    public const string Under = "under";

    private const double MaxAbsMm = 1e6;

    /// <summary>The 3D view of a wall with this spot marked.</summary>
    public string Url(Guid wallId) => $"/walls/{wallId}/3d?at={Uri.EscapeDataString(ToQuery())}";

    /// <summary>Reads <c>facet:a:b:h</c> or <c>facet:a:b:h:under</c>; null for anything else.</summary>
    public static WallSpot? Parse(string? query)
    {
        var parts = query?.Split(':');
        if (parts is not { Length: 4 or 5 } || parts[0].Length is 0 or > 32 || (parts.Length == 5 && parts[4] != Under))
        {
            return null;
        }

        var ok = double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                 & double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                 & double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h);
        return ok && InRange(a) && InRange(b) && InRange(h)
            ? new WallSpot(parts[0], a, b, h, parts.Length == 5 ? Under : null)
            : null;
    }

    /// <summary>Finite and within a sane wall extent (1000 m), so nothing downstream sees 1e308.</summary>
    private static bool InRange(double v) => double.IsFinite(v) && Math.Abs(v) <= MaxAbsMm;

    private string ToQuery() =>
        string.Create(CultureInfo.InvariantCulture, $"{FacetId}:{A:0}:{B:0}:{H:0}") + (Look == Under ? ":" + Under : string.Empty);
}
