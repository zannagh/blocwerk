namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The one inclination range the whole UI uses. The <c>WallSegment</c> entity clamps to
/// 0..90 server-side, so the wall's own fallback angle uses the same range instead of the
/// old 0..70 slider — two different maxima on two sliders that feed the same projection
/// only ever read as a bug.
/// </summary>
public static class WallAngleRange
{
    /// <summary>A vertical plane: no foreshortening at all.</summary>
    public const int Min = 0;

    /// <summary>A horizontal roof: the plane collapses onto its top edge when projected.</summary>
    public const int Max = 90;

    /// <summary>Angles at or above this squash so hard the schematic gets hard to read.</summary>
    public const int SteepWarning = 80;

    /// <summary>A short human label for an inclination, used next to the sliders.</summary>
    public static string Label(int angle) => angle switch
    {
        <= 0 => "vertical",
        < 15 => "slab-ish",
        < 35 => "gentle overhang",
        < 60 => "overhang",
        < 80 => "steep overhang",
        _ => "roof",
    };
}
