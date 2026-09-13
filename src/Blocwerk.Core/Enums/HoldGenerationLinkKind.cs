namespace Blocwerk.Core.Enums;

/// <summary>
/// How an old-generation hold relates to its successor row in the next generation when a whole-wall
/// big update carries holds forward. Separate from <see cref="HoldLinkKind"/>, which ties two holds
/// on adjacent panels within the same generation; this one records forward-in-time lineage.
/// </summary>
public enum HoldGenerationLinkKind
{
    /// <summary>The physical hold is unchanged between the two generations; the successor is a plain carry.</summary>
    Same = 0,

    /// <summary>The user asserted the physical hold changed between the two generations (moved/reshaped/replaced).</summary>
    Changed = 1,
}
