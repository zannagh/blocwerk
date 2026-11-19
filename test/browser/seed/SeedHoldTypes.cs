// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// The Grips wall (Gina's), which only the hold-type spec lists or counts. Holds: two crimps, a jug and two without a
/// type. Boulders, newest last: <b>Crimpy</b> (crimp, crimp, jug: inferred Crimp), <b>Jug set</b> (untyped, crimp, with Jug
/// set on it), <b>Tie</b> (crimp, jug: none), <b>Mostly blank</b> (untyped twice, crimp: under half typed, none), the
/// family <b>Family base</b> (untyped) with its published variant <b>Family crimp</b> (crimp, crimp), which files the
/// family under Crimp, and <b>Override me</b> (no holds, tagged Warmup), on which the spec sets and clears a type. So the
/// cards read Jug 1, Crimp 2 (Crimpy and the family), Pinch, Sloper and Pocket empty; six rows (the variant folds into its family), three of them typed.
/// </summary>
internal static class SeedHoldTypes
{
    public static void Add(BlocwerkDbContext db, Guid wallId, Guid panelId, Guid setterId)
    {
        var crimp1 = Hold(db, wallId, panelId, 0.2, 0.7, HoldHandType.Crimp);
        var crimp2 = Hold(db, wallId, panelId, 0.4, 0.5, HoldHandType.Crimp);
        var jug = Hold(db, wallId, panelId, 0.6, 0.4, HoldHandType.Jug);
        var blank = Hold(db, wallId, panelId, 0.8, 0.3, null);
        var blank2 = Hold(db, wallId, panelId, 0.85, 0.6, null);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var n = 0;

        Boulder Add(string name, Hold[] holds, HoldHandType? dominant = null, Guid? parentId = null)
        {
            var boulder = new Boulder
            {
                WallId = wallId, Name = name, CreatedByUserId = setterId, CreatedAt = start.AddMinutes(n++),
                DominantHoldType = dominant, ParentBoulderId = parentId,
            };
            db.Boulders.Add(boulder);
            db.BoulderHolds.AddRange(holds.Select(h => new BoulderHold { BoulderId = boulder.Id, HoldId = h.Id }));
            return boulder;
        }

        Add("Crimpy", [crimp1, crimp2, jug]);
        Add("Jug set", [blank, crimp1], HoldHandType.Jug);
        Add("Tie", [crimp1, jug]);
        Add("Mostly blank", [blank, blank2, crimp2]);
        var family = Add("Family base", [blank]);
        Add("Family crimp", [crimp1, crimp2], parentId: family.Id);
        Add("Override me", []).Styles = BoulderStyle.Warmup;
    }

    private static Hold Hold(BlocwerkDbContext db, Guid wallId, Guid panelId, double x, double y, HoldHandType? type)
    {
        var hold = new Hold { WallId = wallId, WallPanelId = panelId, X = x, Y = y, Radius = 0.04, HandType = type };
        db.Holds.Add(hold);
        return hold;
    }
}
