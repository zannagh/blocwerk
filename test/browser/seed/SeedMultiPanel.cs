// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// Pia's wall of two panels with a boulder whose holds lie on BOTH panels (the Attic's "Pinky To(e) Pinky"), for the
/// phone layout tests: revising it steps the hold picker between panels. A user and a wall of their own, so no other
/// test's walls, lists or caches change with it.
/// </summary>
internal static class SeedMultiPanel
{
    public static SeededMultiPanel Add(BlocwerkDbContext db, Guid wallId, IReadOnlyList<Guid> panelIds, Guid setterId)
    {
        var boulder = new Boulder { WallId = wallId, Name = "Pinky To(e) Pinky", Grade = "6B", CreatedByUserId = setterId };
        db.Boulders.Add(boulder);
        for (var panel = 0; panel < panelIds.Count; panel++)
        {
            for (var i = 0; i < 3; i++)
            {
                var hold = new Hold { WallId = wallId, WallPanelId = panelIds[panel], X = 0.25 + (0.25 * i), Y = 0.3 + (0.2 * i), Radius = 0.04 };
                db.Holds.Add(hold);
                var type = panel == 0 && i == 0 ? HoldType.Start : panel == panelIds.Count - 1 && i == 2 ? HoldType.Top : HoldType.Normal;
                db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = hold.Id, Type = type });
            }
        }

        return new SeededMultiPanel(wallId, boulder.Id);
    }
}
