// <copyright file="HoldLinkInputs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Blocwerk.Core.HoldLinks;

/// <summary>What a suggestion refresh reads about the wall (<see cref="HoldLinkCandidateLoader"/>).</summary>
/// <param name="ModelId">The active geometry model.</param>
/// <param name="Placed">The live holds that can be compared in 3D (placed, not virtual). Holds flagged "changed" (NeedsReview) count: the flag is about boulders, not the position.</param>
/// <param name="PanelOf">Every live hold's panel photo, placed or not.</param>
/// <param name="Live">Every live hold id.</param>
internal sealed record HoldLinkInputs(
    Guid ModelId, List<HoldLinkCandidate> Placed, Dictionary<Guid, Guid> PanelOf, HashSet<Guid> Live)
{
    /// <summary>
    /// A short hash of everything the finder's answer depends on: model, placed holds (rounded to 0.1 mm), live panel
    /// holds, links and rejections. Same fingerprint, same suggestions: the pairwise search can be skipped.
    /// </summary>
    /// <param name="links">The stored links.</param>
    /// <param name="rejected">The pairs answered "not the same".</param>
    /// <returns>The fingerprint.</returns>
    public string Fingerprint(IEnumerable<(Guid A, Guid B)> links, IEnumerable<(Guid A, Guid B)> rejected)
    {
        var text = new StringBuilder(ModelId.ToString("N"));
        foreach (var h in Placed.OrderBy(h => h.Id))
        {
            text.Append(CultureInfo.InvariantCulture, $"|{h.Id:N},{h.PanelId:N},{h.Color},{h.SizeMm:0.0},{h.IsFoot}");
            text.Append(CultureInfo.InvariantCulture, $",{h.World[0]:0.0},{h.World[1]:0.0},{h.World[2]:0.0}");
        }

        foreach (var (hold, panel) in PanelOf.OrderBy(p => p.Key))
        {
            text.Append(CultureInfo.InvariantCulture, $"|p{hold:N}{panel:N}");
        }

        foreach (var (a, b) in links.Order())
        {
            text.Append(CultureInfo.InvariantCulture, $"|l{a:N}{b:N}");
        }

        foreach (var (a, b) in rejected.Order())
        {
            text.Append(CultureInfo.InvariantCulture, $"|r{a:N}{b:N}");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
