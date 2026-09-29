// <copyright file="CarriedKickboardHoldsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The carried old set is curated, so the mat filter may only drop mat-SIZED auto-detected blobs from it.
/// A real kickboard row low on the photo (below a clear gap, which the floor-gap rule clips), a hand-added
/// hold, a kickboard hold and a foothold are all carried, however mat-like they look.
/// </summary>
public class CarriedKickboardHoldsTests
{
    [Fact]
    public void SelectCarriedMatDrops_KeepsTheKickboardRow_AndEveryHoldAPersonStandsBehind()
    {
        var rng = new Random(7);
        var holds = new List<Hold>();
        for (var i = 0; i < 300; i++)
        {
            holds.Add(Hold(rng.NextDouble(), 0.10 + (rng.NextDouble() * 0.60), 0.006 + (rng.NextDouble() * 0.008), auto: true));
        }

        // The kickboard row: normal-sized footholds well below the wall's hold cloud, some added by hand.
        var kickboard = Enumerable.Range(0, 30)
            .Select(i => Hold(0.03 * (i + 1), 0.93 + (0.001 * (i % 5)), 0.008, auto: i % 2 == 0))
            .ToList();
        holds.AddRange(kickboard);

        var mat = Hold(0.20, 0.95, 0.065, auto: true);
        var handAdded = Hold(0.40, 0.95, 0.065, auto: false);
        var onKickboard = Hold(0.60, 0.95, 0.065, auto: true);
        onKickboard.IsOnKickboard = true;
        var foothold = Hold(0.80, 0.95, 0.065, auto: true);
        foothold.Category = HoldCategory.Foot;
        holds.AddRange([mat, handAdded, onKickboard, foothold]);

        var dropped = WallBigUpdateService.SelectCarriedMatDrops(holds);

        Assert.Equal([mat.Id], dropped);
    }

    private static Hold Hold(double x, double y, double radius, bool auto) => new()
    {
        WallId = Guid.Empty,
        X = x,
        Y = y,
        Radius = radius,
        Generation = 2,
        IsAutoDetected = auto,
    };
}
