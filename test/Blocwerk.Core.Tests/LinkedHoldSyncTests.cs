// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>Linked holds keep colour, material, usage, grip type, kickboard and name in sync across panels.</summary>
public class LinkedHoldSyncTests
{
    private static void MarkKickboardFoot(Hold h)
    {
        h.IsOnKickboard = true;
        h.Category = HoldCategory.Foot;
    }

    private static HoldSyncReport Run(params Hold[] ordered)
    {
        var report = new HoldSyncReport();
        LinkedHoldSyncRules.Apply(ordered, report);
        return report;
    }

    // ---- The rule ---------------------------------------------------------------------------

    [Fact]
    public void Defaults_FillFromTheTwin_ForEveryProperty()
    {
        var main = new Hold
        {
            Name = "Big one", Color = "red", Material = HoldMaterial.PU, HandType = HoldHandType.Jug,
            Category = HoldCategory.Foot, IsOnKickboard = true,
        };
        var twin = new Hold();

        var report = Run(main, twin);

        Assert.Equal(("Big one", "red", HoldMaterial.PU, HoldHandType.Jug, HoldCategory.Foot, true),
            (twin.Name, twin.Color, twin.Material, twin.HandType, twin.Category, twin.IsOnKickboard));
        Assert.Equal(6, report.UpdatedByProperty.Count);
        Assert.Empty(report.Conflicts);
    }

    [Fact]
    public void Defaults_FillTowardTheMainPanel_FromThePeripheralOne()
    {
        var main = new Hold();
        var panel1 = new Hold { IsOnKickboard = true, Category = HoldCategory.Foot };

        Run(main, panel1);

        Assert.True(main.IsOnKickboard);
        Assert.Equal(HoldCategory.Foot, main.Category);
    }

    [Fact]
    public void Conflict_GoesToTheFirstHold_AndIsReported()
    {
        var winner = new Hold { Color = "red" };
        var other = new Hold { Color = "blue" };

        var report = Run(winner, other);

        Assert.Equal("red", other.Color);
        var conflict = Assert.Single(report.Conflicts);
        Assert.Equal(("Color", winner.Id, "red"), (conflict.Property, conflict.KeptHoldId, conflict.Kept));
        Assert.Equal(["blue"], conflict.Replaced);
    }

    [Fact]
    public void AlreadyEqual_ChangesNothing()
    {
        var report = Run(new Hold { Color = "red", IsOnKickboard = true }, new Hold { Color = "red", IsOnKickboard = true });

        Assert.False(report.Any);
        Assert.Equal(1, report.Groups);
    }

    [Fact]
    public void WinnerCell_ConfiguredThenCentreThenNearest()
    {
        (int, int)[] cells = [(1, 0), (0, 0), (2, 0)];
        Assert.Equal((2, 0), LinkedHoldSync.ResolveWinnerCell((2, 0), cells));
        Assert.Equal((0, 0), LinkedHoldSync.ResolveWinnerCell(null, cells));
        Assert.Equal((0, 0), LinkedHoldSync.ResolveWinnerCell((9, 9), cells));
        Assert.Equal((1, 0), LinkedHoldSync.ResolveWinnerCell(null, [(2, 0), (1, 0)]));
        Assert.Null(LinkedHoldSync.ResolveWinnerCell(null, []));
    }

    // ---- Live propagation -------------------------------------------------------------------

    public static TheoryData<string> Properties()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[] { "Color", "Material", "Grip", "Usage", "Kickboard", "Name" })
        {
            data.Add(name);
        }

        return data;
    }

    private static HoldEdit Change(HoldEdit e, string property) => property switch
    {
        "Color" => e with { Color = "green" },
        "Material" => e with { Material = HoldMaterial.Wood },
        "Grip" => e with { HandType = HoldHandType.Crimp },
        "Usage" => e with { Category = HoldCategory.Foot },
        "Kickboard" => e with { IsOnKickboard = true },
        _ => e with { Name = "Pinch of salt" },
    };

    private static object? Read(Hold h, string property) => property switch
    {
        "Color" => h.Color,
        "Material" => h.Material,
        "Grip" => h.HandType,
        "Usage" => h.Category,
        "Kickboard" => h.IsOnKickboard,
        _ => h.Name,
    };

    [Theory]
    [MemberData(nameof(Properties))]
    public async Task Edit_PropagatesEachProperty_InOneBatch_AndUndoRevertsBoth(string property)
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var main = await f.AddPanelAsync(0, 0);
        var second = await f.AddPanelAsync(1, 0);
        var a = await f.AddHoldAsync(main);
        var b = await f.AddHoldAsync(second);
        await f.LinkAsync(a, b);
        var before = Read(await f.GetAsync(b), property);

        await f.EditAsync(b, e => Change(e, property));

        Assert.NotEqual(before, Read(await f.GetAsync(a), property));
        Assert.Equal(Read(await f.GetAsync(b), property), Read(await f.GetAsync(a), property));
        var batch = Assert.Single(await f.BatchesAsync("hold-edit"));

        var reverted = await f.RevertAsync(batch.Id);

        Assert.True(reverted.Reverted, reverted.Error);
        Assert.Equal(before, Read(await f.GetAsync(a), property));
        Assert.Equal(before, Read(await f.GetAsync(b), property));
    }

    [Fact]
    public async Task Edit_ReachesTheWholeChain_Transitively()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var ids = new List<Guid>();
        foreach (var col in new[] { 0, 1, 2 })
        {
            ids.Add(await f.AddHoldAsync(await f.AddPanelAsync(col, 0)));
        }

        await f.LinkAsync(ids[0], ids[1]);
        await f.LinkAsync(ids[1], ids[2]);

        await f.EditAsync(ids[2], e => e with { IsOnKickboard = true, Color = "pink" });

        foreach (var id in ids)
        {
            var hold = await f.GetAsync(id);
            Assert.True(hold.IsOnKickboard);
            Assert.Equal("pink", hold.Color);
        }
    }

    [Fact]
    public async Task Edit_NeverPropagatesPosition_ShapeOrRadius_AndAMoveLeavesTwinPropertiesAlone()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var a = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => h.Color = "red", 0.2, 0.2);
        var b = await f.AddHoldAsync(await f.AddPanelAsync(1, 0), h => h.Color = "blue", 0.7, 0.7);
        await f.LinkAsync(a, b);

        // Move + resize only: nothing shared changed, so the twin's own colour must survive too.
        await f.EditAsync(b, e => e with { X = 0.71, Y = 0.72, Radius = 0.05 });
        var twin = await f.GetAsync(a);
        Assert.Equal((0.2, 0.2, 0.02, "red"), (twin.X, twin.Y, twin.Radius, twin.Color));

        // A real property change still leaves the geometry alone.
        await f.EditAsync(b, e => e with { Color = "green" });
        twin = await f.GetAsync(a);
        Assert.Equal((0.2, 0.2, 0.02, "green"), (twin.X, twin.Y, twin.Radius, twin.Color));
    }

    [Fact]
    public async Task Edit_DoesNotTouchLinkedHoldsOfAnotherGeneration()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var a = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => h.Generation = 5);
        var b = await f.AddHoldAsync(await f.AddPanelAsync(1, 0));
        await f.LinkAsync(a, b);

        await f.EditAsync(b, e => e with { IsOnKickboard = true });

        Assert.False((await f.GetAsync(a)).IsOnKickboard);
    }

    // ---- Reconciliation (admin action) ------------------------------------------------------

    [Fact]
    public async Task Sync_FillsPanel2FromTheMainPanel_AndPanel1FromTheMainPanel()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var main = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => MarkKickboardFoot(h));
        var panel1 = await f.AddHoldAsync(await f.AddPanelAsync(1, 0));
        var panel2 = await f.AddHoldAsync(await f.AddPanelAsync(2, 0));
        await f.LinkAsync(main, panel1);
        await f.LinkAsync(panel1, panel2);

        var result = await f.SyncService().SyncAsync(f.WallId);

        Assert.Equal(2, result.Report.UpdatedByProperty["Kickboard"]);
        foreach (var id in new[] { panel1, panel2 })
        {
            var hold = await f.GetAsync(id);
            Assert.True(hold.IsOnKickboard);
            Assert.Equal(HoldCategory.Foot, hold.Category);
        }
    }

    [Fact]
    public async Task Sync_ConflictsResolveTowardTheCentre_ByDefault_AndTheChosenPanelWhenSet()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var centre = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => h.Color = "red");
        var other = await f.AddHoldAsync(await f.AddPanelAsync(1, 0), h => h.Color = "blue");
        await f.LinkAsync(centre, other);

        var first = await f.SyncService().SyncAsync(f.WallId);

        Assert.Equal("red", (await f.GetAsync(other)).Color);
        var conflict = Assert.Single(first.Report.Conflicts);
        Assert.Equal(centre, conflict.KeptHoldId);

        await f.SyncService().SetWinnerPanelAsync(f.WallId, (1, 0));
        await f.SetAsync(centre, h => h.Color = "red");
        await f.SetAsync(other, h => h.Color = "blue");

        var second = await f.SyncService().SyncAsync(f.WallId);

        Assert.Equal("blue", (await f.GetAsync(centre)).Color);
        Assert.Equal(other, Assert.Single(second.Report.Conflicts).KeptHoldId);
    }

    [Fact]
    public async Task Sync_IsIdempotent_AndItsBatchUndoesExactly()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var a = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => h.IsOnKickboard = true);
        var b = await f.AddHoldAsync(await f.AddPanelAsync(1, 0));
        await f.LinkAsync(a, b);

        var first = await f.SyncService().SyncAsync(f.WallId);
        var second = await f.SyncService().SyncAsync(f.WallId);

        Assert.NotNull(first.BatchId);
        Assert.False(second.Report.Any);
        Assert.Null(second.BatchId);
        Assert.Equal(first.BatchId, (await f.SyncService().GetStatusAsync(f.WallId)).RevertableBatchId);

        var reverted = await f.SyncService().RevertAsync(f.WallId, first.BatchId!.Value);

        Assert.True(reverted.Reverted, reverted.Error);
        Assert.False((await f.GetAsync(b)).IsOnKickboard);
    }

    // ---- Startup ----------------------------------------------------------------------------

    [Fact]
    public async Task Startup_RunsOncePerWall_AndAnUndoIsNotRedoneOnTheNextStart()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var a = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => h.IsOnKickboard = true);
        var b = await f.AddHoldAsync(await f.AddPanelAsync(1, 0));
        await f.LinkAsync(a, b);

        await LinkedHoldSyncStartup.RunIfNeededAsync(f.Journalling, f.Journal, NullLogger.Instance);

        Assert.True((await f.GetAsync(b)).IsOnKickboard);
        Assert.Equal(LinkedHoldSync.Version, (await f.GetWallAsync()).LinkedHoldSyncVersion);
        var batch = Assert.Single(await f.BatchesAsync(LinkedHoldSyncStartup.BatchLabel));

        Assert.True((await f.RevertAsync(batch.Id)).Reverted);
        await LinkedHoldSyncStartup.RunIfNeededAsync(f.Journalling, f.Journal, NullLogger.Instance);

        Assert.False((await f.GetAsync(b)).IsOnKickboard);
        Assert.Single(await f.BatchesAsync(LinkedHoldSyncStartup.BatchLabel));
    }

    // ---- New links --------------------------------------------------------------------------

    [Fact]
    public async Task CreatingALink_FillsTheNewTwinsDefaults()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync();
        var a = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => MarkKickboardFoot(h));
        var b = await f.AddHoldAsync(await f.AddPanelAsync(1, 0));

        await f.PanelService().CreateHoldLinkAsync(f.WallId, b, a);

        var twin = await f.GetAsync(b);
        Assert.True(twin.IsOnKickboard);
        Assert.Equal(HoldCategory.Foot, twin.Category);
    }

    // ---- Heat map ---------------------------------------------------------------------------

    [Fact]
    public void HeatMap_CountsSyncedKickboardTwinsAsOnePhysicalHold()
    {
        var main = Guid.NewGuid();
        var twin = Guid.NewGuid();
        var links = new[] { new HoldLinkPair(main, twin) };
        var boulders = new[]
        {
            new Boulder { Name = "k", KickboardFootholdsOn = true, BoulderHolds = [new BoulderHold { HoldId = main, Type = HoldType.Normal }] },
            new Boulder { Name = "plain", BoulderHolds = [new BoulderHold { HoldId = twin, Type = HoldType.Normal }] },
        };

        // Both twins are flagged now. The per-hold count is shared by the twins and is not doubled.
        var counts = WallUsageHeat.CountByHold(boulders, links, [main, twin]);

        Assert.Equal(2, counts[main]);
        Assert.Equal(2, counts[twin]);
        Assert.Equal(2, counts.Values.Max());
    }
}
