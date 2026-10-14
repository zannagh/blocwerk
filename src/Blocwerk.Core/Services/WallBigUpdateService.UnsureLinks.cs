// <copyright file="WallBigUpdateService.UnsureLinks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The links a full revision made without being sure (<see cref="ConfirmedLink.Unsure"/>): each one this promote
/// actually created becomes a look-over finding that asks whether it is the same hold on both photos.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// One <see cref="LookOverFindingKind.UnsureLink"/> finding per unsure link the promote added (read from the change
    /// tracker before it saves): about the neighbour panel's hold, with the centre hold as the other one.
    /// </summary>
    /// <param name="db">The promote's context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="confirmation">The decisions the promote applied.</param>
    /// <returns>The findings, not added to the context yet.</returns>
    internal static List<WallLookOverFinding> UnsureLinkFindings(BlocwerkDbContext db, Guid wallId, BigUpdateConfirmation confirmation)
    {
        var added = db.HoldLinks.Local
            .Where(l => l.WallId == wallId && db.Entry(l).State == EntityState.Added)
            .Select(l => (l.HoldAId, l.HoldBId))
            .ToHashSet();
        var holds = db.Holds.Local.Where(h => h.WallId == wallId).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());
        var findings = new List<WallLookOverFinding>();
        foreach (var set in confirmation.Neighbours)
        {
            foreach (var link in set.Links.Where(l => l.Unsure && added.Contains((l.NeighborHoldId, l.NewHoldId))))
            {
                if (!holds.TryGetValue(link.NewHoldId, out var hold))
                {
                    continue;
                }

                findings.Add(new WallLookOverFinding
                {
                    Kind = LookOverFindingKind.UnsureLink,
                    HoldId = hold.Id,
                    OtherHoldId = link.NeighborHoldId,
                    PanelId = set.PanelId,
                    X = hold.X,
                    Y = hold.Y,
                    Radius = hold.Radius,
                    ShapePoints = hold.ShapePoints?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList(),
                });
            }
        }

        return findings;
    }
}
