using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// One of a boulder's holds, translated to the row that represents it at a particular wall
/// generation. <see cref="Hold"/> is the row AT that generation — so it carries that generation's
/// panel id and panel-local coordinates — while <see cref="Type"/> and <see cref="Usage"/> stay the
/// boulder's own marks, which belong to the boulder and not to any generation of the wall.
/// </summary>
/// <param name="Hold">The hold row at the requested generation.</param>
/// <param name="Type">The boulder's mark on the hold (start/top/normal).</param>
/// <param name="Usage">The boulder's usage rule for the hold (hand and foot, foot only, ...).</param>
public record BoulderHoldAtGeneration(Hold Hold, HoldType Type, HoldUsage Usage)
{
    /// <summary>The id of the hold row at the requested generation.</summary>
    public Guid HoldId => Hold.Id;

    /// <summary>
    /// The boulder's OWN hold ids that resolved to this row. Usually one; SEVERAL when the lineage
    /// records that this one older hold later became each of them (the merge case), which is the
    /// only way to tell a genuine translation failure from a convergence — subtracting set sizes
    /// reports the convergence as a missing hold. Defaults to the row itself, which is exactly right
    /// for the untranslated "Now" view, where every hold is its own source.
    /// </summary>
    public IReadOnlyList<Guid> SourceHoldIds { get; init; } = [Hold.Id];
}
