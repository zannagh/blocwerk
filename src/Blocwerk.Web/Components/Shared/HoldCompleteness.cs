using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What the editor's "missing information" overlay may look for. A flag per criterion, because a
/// single fixed predicate cannot be right for every wall: see <see cref="HoldCompleteness"/>.
/// </summary>
[Flags]
public enum HoldCompletenessCriteria
{
    /// <summary>Overlay off — nothing is badged.</summary>
    None = 0,

    /// <summary>Badge holds with no colour assigned. The one criterion that is almost always wanted.</summary>
    Color = 1,

    /// <summary>
    /// Badge hand holds with no grip sub-type. Opt-in: nothing ever stamps a sub-type, so on a wall
    /// whose owner does not record them this is true of every hand hold at once.
    /// </summary>
    HandType = 2,
}

/// <summary>
/// Decides whether a hold is still missing information the owner has to supply by hand.
/// <para>
/// Deliberately narrow, because only two fields can be told apart from a never-touched default:
/// <see cref="Hold.Color"/> (nullable, and nothing ever stamps one for you) and
/// <see cref="Hold.HandType"/> (nullable, but only meaningful on a hand hold).
/// <see cref="Hold.Category"/> is non-nullable with <c>Hand = 0</c>, so "no category" and
/// "deliberately a hand hold" are the same value, and <see cref="Hold.Material"/> is always
/// stamped by the Add tool, so a set material was never necessarily a decision. Neither is
/// used here — an overlay that claimed to find them would be lying.
/// </para>
/// <para>
/// The criteria are CHOSEN, and the default is colour alone (<see cref="Default"/>). "Missing"
/// is not the same thing as "wanted": nothing ever stamps a <see cref="Hold.HandType"/>, so on a
/// mature wall whose owner records grip sub-types for a handful of project holds, folding that
/// criterion in unconditionally badged nearly every hand hold — a sea of "?" markers that could
/// never be worked down and that drowned the holds genuinely missing a colour. Worse, a hold left
/// uncoloured on purpose had no way to be dismissed. Colour alone is the criterion an owner can
/// actually finish; sub-type is available for the pass where they want it.
/// </para>
/// <para>
/// Still honest about its limits: neither criterion can distinguish "not filled in yet" from
/// "deliberately left blank", and the overlay has no way to record the latter. It is a finding
/// aid, not a worklist that can be driven to zero.
/// </para>
/// </summary>
public static class HoldCompleteness
{
    /// <summary>
    /// What the overlay looks for unless the user picks otherwise: colour only.
    /// </summary>
    public const HoldCompletenessCriteria Default = HoldCompletenessCriteria.Color;

    /// <summary>True when the hold has no colour assigned.</summary>
    public static bool MissingColor(Hold hold) => string.IsNullOrEmpty(hold.Color);

    /// <summary>True when a hand hold has no grip sub-type. Always false for foot holds.</summary>
    public static bool MissingHandType(Hold hold) =>
        hold.Category == HoldCategory.Hand && hold.HandType is null;

    /// <summary>
    /// True when the hold has no traced outline. Optional: an untraced hold is still a usable
    /// circle, so this is reported separately and never folded into <see cref="IsIncomplete"/>.
    /// </summary>
    public static bool MissingShape(Hold hold) => hold.ShapePoints is not { Count: >= 3 };

    /// <summary>
    /// The overlay's predicate under the chosen <paramref name="criteria"/>. Always false for
    /// <see cref="HoldCompletenessCriteria.None"/>, so the overlay being off needs no second check.
    /// </summary>
    public static bool IsIncomplete(Hold hold, HoldCompletenessCriteria criteria)
    {
        if (criteria.HasFlag(HoldCompletenessCriteria.Color) && MissingColor(hold))
        {
            return true;
        }

        return criteria.HasFlag(HoldCompletenessCriteria.HandType) && MissingHandType(hold);
    }

    /// <summary>How many of <paramref name="holds"/> are incomplete under <paramref name="criteria"/>.</summary>
    public static int CountIncomplete(IEnumerable<Hold> holds, HoldCompletenessCriteria criteria) =>
        criteria == HoldCompletenessCriteria.None ? 0 : holds.Count(h => IsIncomplete(h, criteria));

    /// <summary>
    /// The toggle's cycle: off → colour → colour and sub-type → off. One control, so the owner can
    /// widen the search for a pass and drop back without a settings surface of its own.
    /// </summary>
    public static HoldCompletenessCriteria Next(HoldCompletenessCriteria criteria) => criteria switch
    {
        HoldCompletenessCriteria.None => HoldCompletenessCriteria.Color,
        HoldCompletenessCriteria.Color => HoldCompletenessCriteria.Color | HoldCompletenessCriteria.HandType,
        _ => HoldCompletenessCriteria.None,
    };

    /// <summary>
    /// One wording for the toggle, the count badge and the help page, so none of them can overstate
    /// what the overlay is able to detect.
    /// </summary>
    public static string Describe(HoldCompletenessCriteria criteria) => criteria switch
    {
        HoldCompletenessCriteria.None =>
            "Missing-information overlay (I): off. Press again to badge holds with no colour.",
        HoldCompletenessCriteria.Color =>
            "Missing-information overlay (I): holds with no colour set. Press again to include hand holds "
            + "with no sub-type. Type and material cannot be checked — every hold gets one by default. "
            + "Tap a badged hold in Select / Move to fill it in.",
        _ =>
            "Missing-information overlay (I): holds with no colour set, or hand holds with no sub-type. "
            + "Nothing ever stamps a sub-type, so on a mature wall most hand holds will badge. Press again "
            + "to turn the overlay off.",
    };
}
