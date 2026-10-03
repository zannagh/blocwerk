// <copyright file="CarriedMatPerPanelTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The mat filter over the carried old holds is population-relative on coordinates normalized to ONE photo,
/// so it must classify each panel's holds on their own. Pooling every re-shot panel made the large holds
/// at the bottom of a panel shot from closer look like floor mats next to another panel's small ones.
/// </summary>
public class CarriedMatPerPanelTests
{
    [Fact]
    public void SelectCarriedMatDrops_ClassifiesEachPanelOnItsOwn()
    {
        var far = Guid.NewGuid();
        var close = Guid.NewGuid();
        var holds = new List<Hold>();
        for (var i = 0; i < 100; i++)
        {
            holds.Add(Hold(far, 0.01 * i, 0.10 + (0.005 * i), 0.007 + (0.0005 * (i % 5))));
        }

        // A panel shot from closer: bigger holds, running all the way down the photo.
        for (var i = 0; i < 20; i++)
        {
            holds.Add(Hold(close, 0.05 * i, 0.10 + (0.047 * i), 0.020 + (0.0005 * i)));
        }

        // NON-VACUOUS: pooled, the classifier calls the close panel's bottom holds mats.
        var pooled = MatFalseDetectionFilter.Classify(
            holds.Select(x => new DetectedHold(x.X, x.Y, x.Radius, x.Color, x.Confidence)).ToList());
        Assert.NotEmpty(pooled.RadiusMats);

        Assert.Empty(WallBigUpdateService.SelectCarriedMatDrops(holds));
    }

    [Fact]
    public void SelectCarriedMatDrops_StillDropsAMatOnItsOwnPanel_ButNeverANamedHold()
    {
        var panel = Guid.NewGuid();
        var holds = new List<Hold>();
        for (var i = 0; i < 100; i++)
        {
            holds.Add(Hold(panel, 0.01 * i, 0.10 + (0.0075 * i), 0.008 + (0.0005 * (i % 5))));
        }

        var mat = Hold(panel, 0.30, 0.97, 0.08);
        var named = Hold(panel, 0.70, 0.97, 0.08);
        named.Name = "Volume foot";
        holds.AddRange([mat, named]);

        Assert.Equal([mat.Id], WallBigUpdateService.SelectCarriedMatDrops(holds));
    }

    private static Hold Hold(Guid panelId, double x, double y, double radius) => new()
    {
        WallId = Guid.Empty,
        WallPanelId = panelId,
        X = x,
        Y = y,
        Radius = radius,
        Generation = 2,
        IsAutoDetected = true,
    };
}
