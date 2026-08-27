using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The single source of truth for the colors a boulder's holds are drawn in.
/// The hold <see cref="HoldType"/> triad (start / normal / top) used to be copy-pasted
/// across the picker, the revise reference overlay and the detail page; it lives here
/// now, together with the foot color introduced by the foothold rules.
/// </summary>
public static class BoulderHoldColors
{
    /// <summary>Start holds.</summary>
    public const string Start = "#4CAF50";

    /// <summary>Top holds.</summary>
    public const string Top = "#9C27B0";

    /// <summary>Every other hold of the boulder.</summary>
    public const string Normal = "#2196F3";

    /// <summary>
    /// Dedicated footholds. A warm orange, deliberately far from the light lavender
    /// <c>#c86dff</c> the wall editor uses for *virtual* holds (which is also only ever
    /// drawn as a dashed outline, never as a fill). Keep in sync with the
    /// <c>--foot-hold</c> custom property in <c>wwwroot/css/components.css</c>.
    /// </summary>
    public const string Foot = "#FF9800";

    /// <summary>The stroke color for a hold of the given type.</summary>
    public static string Stroke(HoldType type) => type switch
    {
        HoldType.Start => Start,
        HoldType.Top => Top,
        _ => Normal,
    };

    /// <summary>The translucent fill for a hold of the given type.</summary>
    public static string Fill(HoldType type, double alpha = 0.55) =>
        HoldPalette.Rgba(Stroke(type), alpha);

    /// <summary>
    /// The fill a selected hold gets. The fill carries the usage, the stroke carries the
    /// type: a foot-only hold reads orange, a hand-only hold is left near-hollow (there is
    /// nothing to stand on), and both keep their start/normal/top ring. The hollow fill is
    /// faint rather than "none" so the shape still catches taps.
    /// </summary>
    public static string FillFor(HoldType type, HoldUsage usage, double alpha = 0.55) => usage switch
    {
        HoldUsage.FootOnly => HoldPalette.Rgba(Foot, alpha),
        HoldUsage.HandOnly => Fill(type, alpha * 0.15),
        _ => Fill(type, alpha),
    };

    /// <summary>
    /// A hold that is a foothold purely because it matches the boulder's
    /// "feet of one color only" rule: orange through and through, it has no type.
    /// </summary>
    public static string ColorRuleFill(double alpha = 0.4) => HoldPalette.Rgba(Foot, alpha);
}
