// <copyright file="MarkerIdPolicy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Which <see cref="ArucoDict4X4"/> ids the app hands out, in which order, and which it no longer trusts.
/// </summary>
/// <remarks>
/// <para>
/// Id 17 is RETIRED. Its payload is a single 2 × 2 white block (4 white cells, the sparsest id), so a dark hold with a
/// bright bolt or t-nut decodes as it under some rotation: on The Attic (2026-10-26) about 21 % of its 104 capture
/// detections were holds, the same holds again in neighbouring photos; the ones that passed the quiet-zone check bent
/// the solve to 4.98 px (3.55 px without 17). No other id is at Hamming distance 0 from "one bright block on dark", and
/// the detector corrects no bits, so 17 is the only id retired. It is never issued, and a capture detection of it is
/// ignored before the solver (<c>CaptureRetiredMarkers</c>), so a sheet still on the wall cannot do that again.
/// </para>
/// <para>
/// Ids 41 and 37 are issued LAST: holds decode as them too (41: 25 detections without a white border against 10
/// accepted over two Attic captures; 37 is one big 9-cell white blob, a purple hold read as it), but the quiet-zone
/// check has caught those so far. A plan only uses them when every other id is taken.
/// </para>
/// </remarks>
public static class MarkerIdPolicy
{
    /// <summary>Ids never issued and ignored in capture photos.</summary>
    public static readonly IReadOnlySet<int> Retired = new HashSet<int> { 17 };

    /// <summary>Ids issued only when every other id is taken, in this order.</summary>
    public static readonly IReadOnlyList<int> IssuedLast = [41, 37];

    /// <summary>Every id that may be issued, in the order it is issued.</summary>
    public static IReadOnlyList<int> IssueOrder { get; } = Enumerable.Range(0, ArucoDict4X4.Count)
        .Where(id => !Retired.Contains(id) && !IssuedLast.Contains(id))
        .Concat(IssuedLast)
        .ToList();

    /// <summary>True for an id the app no longer trusts (<see cref="Retired"/>).</summary>
    public static bool IsRetired(int id) => Retired.Contains(id);

    /// <summary>
    /// The id of the <paramref name="index"/>-th issued marker. Past the dictionary it continues above
    /// <see cref="ArucoDict4X4.Count"/>, which the validator reports as out of range, as before.
    /// </summary>
    public static int IdAt(int index) => index < IssueOrder.Count
        ? IssueOrder[index]
        : ArucoDict4X4.Count + (index - IssueOrder.Count);

    /// <summary>The first id of <see cref="IssueOrder"/> not in <paramref name="used"/>; past the dictionary when all are.</summary>
    public static int NextFree(IEnumerable<int> used)
    {
        var taken = used.ToHashSet();
        for (var i = 0; ; i++)
        {
            if (!taken.Contains(IdAt(i)))
            {
                return IdAt(i);
            }
        }
    }
}
