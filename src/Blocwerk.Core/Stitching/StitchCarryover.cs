using System.Text.Json.Serialization;

namespace Blocwerk.Core.Stitching;

/// <summary>
/// The match of the wall's current holds onto the new master.
/// </summary>
/// <remarks>
/// <see cref="Blocker"/> is the pipeline's own standing caveat about how accurate this match is. It
/// is non-empty whenever carryover ran at all, and while it is set the result must never be applied
/// to the LIVE wall unattended: show the operator the overlay and make them confirm. Staging it is
/// fine — staging is what the review happens on.
/// </remarks>
public sealed record StitchCarryover(
    int Generation,
    IReadOnlyDictionary<string, int>? Counts,
    IReadOnlyDictionary<string, int>? CountsBoulderLinked,
    double EstimatedPrecision,
    string? Blocker,
    IReadOnlyList<StitchCarriedHold>? Carried,
    IReadOnlyList<StitchCarriedHold>? Missing,

    // "new" is a C# keyword, so the wire name has to be spelled out explicitly.
    [property: JsonPropertyName("new")] IReadOnlyList<StitchNewHold>? New);
