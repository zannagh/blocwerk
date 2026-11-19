// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// The variants wall (Vera's): a family of a parent (6A), two published variants (5+ and 7A) and a draft variant (8A) that
/// must not count; Origin (dedicated footholds and a custom rule), which the Add Variant test starts from; and Mark start and Mark other,
/// which the Mark Variant test links and unlinks. Every boulder uses the same four outlined holds, rising from left to
/// right; a fifth at the far right belongs to none, for adding to a variant.
/// </summary>
internal static class SeedVariants
{
    public static void Add(BlocwerkDbContext db, Guid wallId, Guid panelId, Guid setterId)
    {
        var holds = Enumerable.Range(0, 4).Select(i => OutlinedHold(wallId, panelId, 0.15 + (0.2 * i), 0.7 - (0.15 * i))).ToList();
        db.Holds.AddRange(holds);
        db.Holds.Add(OutlinedHold(wallId, panelId, 0.88, 0.6));

        Boulder Add(string name, string grade, Guid? parentId = null, bool isDraft = false)
        {
            var boulder = new Boulder { WallId = wallId, Name = name, Grade = grade, CreatedByUserId = setterId, ParentBoulderId = parentId, IsDraft = isDraft };
            db.Boulders.Add(boulder);
            db.BoulderHolds.AddRange(holds.Select((h, i) => new BoulderHold
            {
                BoulderId = boulder.Id,
                HoldId = h.Id,
                Type = i == 0 ? HoldType.Start : i == holds.Count - 1 ? HoldType.Top : HoldType.Normal,
            }));
            return boulder;
        }

        var parent = Add("Family parent", "6A");
        Add("Family hard", "7A", parent.Id);
        Add("Family easy", "5+", parent.Id);
        Add("Family draft", "8A", parent.Id, isDraft: true);
        var origin = Add("Origin", "6B");
        (origin.HandsFollowFeet, origin.CustomRule) = (false, "Start sitting");
        Add("Mark start", "6C");
        Add("Mark other", "7B");
    }

    // A six-sided outline, as the hold detection leaves on a photographed hold.
    private static Hold OutlinedHold(Guid wallId, Guid panelId, double x, double y) => new()
    {
        WallId = wallId,
        WallPanelId = panelId,
        X = x,
        Y = y,
        Radius = 0.04,
        ShapePoints = Enumerable.Range(0, 6)
            .Select(i => new ShapePoint { Dx = 0.04 * Math.Cos(i * Math.PI / 3), Dy = 0.07 * Math.Sin(i * Math.PI / 3) })
            .ToList(),
    };
}
