// <copyright file="RedetectStamps.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// The stamp of a proposal: what the person saw on its card. It covers the stored holds the proposal is about (place,
/// size, outline, name, colour, what they sit on) and the detected outlines it would write. A proposal is applied only
/// while its stamp is still the one that was shown, so an accepted card never writes anything else than its picture.
/// </summary>
internal static class RedetectStamps
{
    /// <summary>The stamp of a proposal against the holds and detections it would be applied to.</summary>
    /// <param name="proposal">The proposal.</param>
    /// <param name="holds">The stored holds by id.</param>
    /// <param name="detections">The detections.</param>
    /// <returns>The stamp.</returns>
    public static string Of(RedetectProposal proposal, IReadOnlyDictionary<Guid, Hold> holds, IReadOnlyList<RedetectDetection> detections)
    {
        var text = new StringBuilder(proposal.Key).Append('|').Append(proposal.Kind);
        var ids = proposal.OtherHoldIds.AsEnumerable();
        foreach (var id in proposal.HoldId is { } own ? ids.Prepend(own) : ids)
        {
            text.Append("|h:");
            if (holds.TryGetValue(id, out var hold))
            {
                Describe(text, hold).Append(';').Append(hold.Name).Append(';').Append(hold.Color).Append(';').Append(hold.StackedOnHoldId);
            }
        }

        foreach (int index in proposal.Detections)
        {
            Describe(text.Append("|d:"), detections[index].Shape);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 12);
    }

    private static StringBuilder Describe(StringBuilder text, Hold hold)
    {
        text.Append(hold.Id).Append(';').Append(Number(hold.X)).Append(';').Append(Number(hold.Y)).Append(';').Append(Number(hold.Radius));
        foreach (var point in hold.ShapePoints ?? [])
        {
            text.Append(';').Append(Number(point.Dx)).Append(',').Append(Number(point.Dy));
        }

        return text;
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
