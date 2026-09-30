// <copyright file="HoldLinkSuggestionFinder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.HoldLinks;

/// <summary>
/// Finds holds of DIFFERENT panel photos that sit on the same spot in 3D but are not linked: the same physical hold
/// seen on two overlapping photos. Pure; the caller hands in the placed live holds, the stored links and the pairs
/// the admin already answered "not the same". Nothing is linked here.
/// </summary>
public static class HoldLinkSuggestionFinder
{
    /// <summary>Centres at most this far apart, whatever the holds' size, mm.</summary>
    public const double MinDistanceMm = 60;

    /// <summary>Centres at most this fraction of the two sizes added up apart.</summary>
    public const double SizeFraction = 0.5;

    /// <summary>Two measured sizes must not differ by more than this factor.</summary>
    public const double MaxSizeRatio = 2.0;

    /// <summary>The suggestions, closest first; each hold is suggested at most once per other panel.</summary>
    /// <param name="holds">The placed live holds.</param>
    /// <param name="links">The wall's stored links (any kind).</param>
    /// <param name="rejected">Pairs answered "not the same", in <see cref="HoldLinkPairSuggestion.Key"/> order.</param>
    /// <param name="panelOf">Every live hold's panel (placed or not); defaults to the placed holds' panels.</param>
    /// <returns>The suggested pairs.</returns>
    public static List<HoldLinkPairSuggestion> Find(
        IReadOnlyList<HoldLinkCandidate> holds,
        IEnumerable<(Guid A, Guid B)> links,
        IReadOnlySet<(Guid A, Guid B)> rejected,
        IReadOnlyDictionary<Guid, Guid>? panelOf = null)
    {
        var groups = new LinkGroups(panelOf ?? holds.ToDictionary(h => h.Id, h => h.PanelId), links);
        var taken = new HashSet<(Guid Hold, Guid Panel)>();
        var result = new List<HoldLinkPairSuggestion>();
        foreach (var (a, b, d) in ClosePairs(holds).OrderBy(p => p.D).ThenBy(p => p.A.Id).ThenBy(p => p.B.Id))
        {
            var key = HoldLinkPairSuggestion.Key(a.Id, b.Id);
            if (rejected.Contains(key) || !groups.MayLink(a.Id, a.PanelId, b.Id, b.PanelId)
                || taken.Contains((a.Id, b.PanelId)) || taken.Contains((b.Id, a.PanelId)))
            {
                continue;
            }

            taken.Add((a.Id, b.PanelId));
            taken.Add((b.Id, a.PanelId));
            result.Add(new HoldLinkPairSuggestion(key.A, key.B, Math.Round(d, 1)));
        }

        return result;
    }

    /// <summary>
    /// True when two holds of different panels may be one hold: within max(<see cref="MinDistanceMm"/>,
    /// <see cref="SizeFraction"/> × both sizes), same colour when both are known, measured sizes within
    /// <see cref="MaxSizeRatio"/>.
    /// </summary>
    /// <param name="a">One hold.</param>
    /// <param name="b">The other.</param>
    /// <param name="distanceMm">Their 3D distance, mm.</param>
    /// <returns>Whether they look like the same hold.</returns>
    public static bool AreLookAlikes(HoldLinkCandidate a, HoldLinkCandidate b, out double distanceMm)
    {
        distanceMm = Distance(a.World, b.World);
        if (a.PanelId == b.PanelId || distanceMm > Math.Max(MinDistanceMm, SizeFraction * (SizeOf(a) + SizeOf(b))))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(a.Color) && !string.IsNullOrEmpty(b.Color)
            && !string.Equals(a.Color, b.Color, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (a.SizeMm is not > 0 || b.SizeMm is not > 0)
        {
            return true;
        }

        return Math.Max(a.SizeMm.Value, b.SizeMm.Value) / Math.Min(a.SizeMm.Value, b.SizeMm.Value) <= MaxSizeRatio;
    }

    private static IEnumerable<(HoldLinkCandidate A, HoldLinkCandidate B, double D)> ClosePairs(IReadOnlyList<HoldLinkCandidate> holds)
    {
        for (var i = 0; i < holds.Count; i++)
        {
            for (var j = i + 1; j < holds.Count; j++)
            {
                if (AreLookAlikes(holds[i], holds[j], out var d))
                {
                    yield return (holds[i], holds[j], d);
                }
            }
        }
    }

    private static double SizeOf(HoldLinkCandidate h) =>
        h.SizeMm is > 0 ? h.SizeMm.Value : h.IsFoot ? Wall3DViewBuilder.DefaultFootSizeMm : Wall3DViewBuilder.DefaultHandSizeMm;

    private static double Distance(double[] a, double[] b) =>
        Math.Sqrt(((a[0] - b[0]) * (a[0] - b[0])) + ((a[1] - b[1]) * (a[1] - b[1])) + ((a[2] - b[2]) * (a[2] - b[2])));
}
