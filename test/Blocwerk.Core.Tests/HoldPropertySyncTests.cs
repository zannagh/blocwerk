using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for linked-hold appearance sync: the pure helper (copy semantics, centrality tie-break,
/// connected components), the one-time <see cref="HoldPropertyBackfill"/>, the link-time copy in
/// <see cref="WallPanelService.CreateHoldLinkAsync"/>, and the live-edit propagation in
/// <see cref="WallService.UpdateHoldAsync"/>.
/// </summary>
public class HoldPropertySyncTests
{
    // ---- Pure helper ----------------------------------------------------------------------

    [Fact]
    public void CopyAppearance_DifferingFields_CopiesAllFourVerbatim()
    {
        var source = new Hold { Color = "red", Material = HoldMaterial.PU, Category = HoldCategory.Foot, HandType = HoldHandType.Crimp };
        var target = new Hold { Color = "blue", Material = HoldMaterial.Wood, Category = HoldCategory.Hand, HandType = HoldHandType.Jug };

        var changed = HoldPropertySync.CopyAppearance(source, target);

        Assert.True(changed);
        Assert.Equal("red", target.Color);
        Assert.Equal(HoldMaterial.PU, target.Material);
        Assert.Equal(HoldCategory.Foot, target.Category);
        Assert.Equal(HoldHandType.Crimp, target.HandType);
    }

    [Fact]
    public void CopyAppearance_NullSource_ClearsTarget_CentreWinsFully()
    {
        var source = new Hold { Color = null, Material = null, Category = HoldCategory.Hand, HandType = null };
        var target = new Hold { Color = "blue", Material = HoldMaterial.PE, Category = HoldCategory.Hand, HandType = HoldHandType.Sloper };

        var changed = HoldPropertySync.CopyAppearance(source, target);

        Assert.True(changed);
        Assert.Null(target.Color);
        Assert.Null(target.Material);
        Assert.Null(target.HandType);
    }

    [Fact]
    public void CopyAppearance_AlreadyEqual_IsNoOp()
    {
        var source = new Hold { Color = "red", Material = HoldMaterial.PU, Category = HoldCategory.Hand, HandType = HoldHandType.Jug };
        var target = new Hold { Color = "red", Material = HoldMaterial.PU, Category = HoldCategory.Hand, HandType = HoldHandType.Jug };

        Assert.False(HoldPropertySync.CopyAppearance(source, target));
    }

    [Fact]
    public void CopyAppearance_NeverTouchesGeometryPositionOrLifecycle()
    {
        var source = new Hold { Color = "red", X = 0.9, Y = 0.9, Radius = 0.5, Name = "src", IsOnKickboard = true, NeedsReview = true, Generation = 7 };
        var target = new Hold { Color = "blue", X = 0.1, Y = 0.2, Radius = 0.02, Name = "tgt", IsOnKickboard = false, NeedsReview = false, Generation = 3 };

        HoldPropertySync.CopyAppearance(source, target);

        Assert.Equal(0.1, target.X);
        Assert.Equal(0.2, target.Y);
        Assert.Equal(0.02, target.Radius);
        Assert.Equal("tgt", target.Name);
        Assert.False(target.IsOnKickboard);
        Assert.False(target.NeedsReview);
        Assert.Equal(3, target.Generation);
    }

    [Fact]
    public void MostCentral_PicksLowestCentrality()
    {
        var a = new HoldCentrality(Guid.NewGuid(), 2, 0);
        var centre = new HoldCentrality(Guid.NewGuid(), 0, 0);
        var b = new HoldCentrality(Guid.NewGuid(), 1, 1);

        Assert.Equal(centre.HoldId, HoldPropertySync.MostCentral(new[] { a, centre, b }).HoldId);
    }

    [Fact]
    public void MostCentral_NullPanel_IsCentre()
    {
        var legacy = new HoldCentrality(Guid.NewGuid(), null, null);
        var onGrid = new HoldCentrality(Guid.NewGuid(), 1, 0);

        Assert.Equal(legacy.HoldId, HoldPropertySync.MostCentral(new[] { onGrid, legacy }).HoldId);
    }

    [Fact]
    public void MostCentral_TieOnCentrality_BreaksByLowestColThenRowThenId()
    {
        // Both centrality 1: (-1,0) wins on lower Col regardless of input order.
        var left = new HoldCentrality(Guid.NewGuid(), -1, 0);
        var right = new HoldCentrality(Guid.NewGuid(), 1, 0);

        Assert.Equal(left.HoldId, HoldPropertySync.MostCentral(new[] { right, left }).HoldId);
        Assert.Equal(left.HoldId, HoldPropertySync.MostCentral(new[] { left, right }).HoldId);
    }

    [Fact]
    public void MostCentral_FullTie_BreaksByLowestId()
    {
        var lowId = new Guid("00000000-0000-0000-0000-000000000001");
        var highId = new Guid("ffffffff-0000-0000-0000-000000000000");
        var a = new HoldCentrality(lowId, 1, 0);
        var b = new HoldCentrality(highId, 1, 0);

        Assert.Equal(lowId, HoldPropertySync.MostCentral(new[] { b, a }).HoldId);
    }

    [Fact]
    public void ConnectedComponents_CollapsesTransitiveChain()
    {
        var centre = Guid.NewGuid();
        var right = Guid.NewGuid();
        var right2 = Guid.NewGuid();
        var lonely = Guid.NewGuid();
        var links = new[] { new HoldLinkPair(centre, right), new HoldLinkPair(right, right2) };

        var components = HoldPropertySync.ConnectedComponents(new[] { centre, right, right2, lonely }, links);

        var chain = Assert.Single(components, c => c.Contains(centre));
        Assert.Equal(new HashSet<Guid> { centre, right, right2 }, chain);
        Assert.Single(components, c => c.SetEquals(new[] { lonely }));
    }

    // ---- Backfill -------------------------------------------------------------------------

    [Fact]
    public async Task Backfill_TransitiveChain_PropagatesCentreToAll_AndIsIdempotent()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var (centre, right, right2) = await SeedChainAsync(
            h,
            centre: new Appearance("red", HoldMaterial.PU, HoldCategory.Hand, HoldHandType.Crimp),
            right: new Appearance("blue", HoldMaterial.Wood, HoldCategory.Foot, HoldHandType.Jug),
            right2: new Appearance("green", HoldMaterial.PE, HoldCategory.Foot, HoldHandType.Sloper));

        await HoldPropertyBackfill.RunIfNeededAsync(h.DbContextFactory, NullLogger.Instance);

        await AssertAllEqualAsync(h, "red", HoldMaterial.PU, HoldCategory.Hand, HoldHandType.Crimp, centre, right, right2);

        // A second run must not change any hold's UpdatedAt-equivalent state: values stay identical.
        var before = await SnapshotAsync(h, centre, right, right2);
        await HoldPropertyBackfill.RunIfNeededAsync(h.DbContextFactory, NullLogger.Instance);
        var after = await SnapshotAsync(h, centre, right, right2);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Backfill_NullCentreValue_ClearsPeripheral_CentreWinsFully()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var (_, right, _) = await SeedChainAsync(
            h,
            centre: new Appearance(null, null, HoldCategory.Hand, null),
            right: new Appearance("blue", HoldMaterial.PE, HoldCategory.Hand, HoldHandType.Sloper),
            right2: null);

        await HoldPropertyBackfill.RunIfNeededAsync(h.DbContextFactory, NullLogger.Instance);

        await using var db = h.CreateContext();
        var peripheral = await db.Holds.FirstAsync(x => x.Id == right);
        Assert.Null(peripheral.Color);
        Assert.Null(peripheral.Material);
        Assert.Null(peripheral.HandType);
    }

    [Fact]
    public async Task Backfill_CentralityTie_PicksDeterministicSource()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        // Two holds tie on centrality 1: panel (-1,0) wins on lower Col, so its "left" colour spreads.
        Guid leftId;
        Guid rightId;
        await using (var db = h.CreateContext())
        {
            var left = NewPanel(h.WallId, -1, 0);
            var right = NewPanel(h.WallId, 1, 0);
            db.WallPanels.AddRange(left, right);

            var leftHold = NewHold(h.WallId, left.Id, "left-colour");
            var rightHold = NewHold(h.WallId, right.Id, "right-colour");
            db.Holds.AddRange(leftHold, rightHold);
            db.HoldLinks.Add(NewLink(h.WallId, leftHold.Id, rightHold.Id));
            await db.SaveChangesAsync();
            leftId = leftHold.Id;
            rightId = rightHold.Id;
        }

        await HoldPropertyBackfill.RunIfNeededAsync(h.DbContextFactory, NullLogger.Instance);

        await using var check = h.CreateContext();
        Assert.Equal("left-colour", (await check.Holds.FirstAsync(x => x.Id == leftId)).Color);
        Assert.Equal("left-colour", (await check.Holds.FirstAsync(x => x.Id == rightId)).Color);
    }

    // ---- CreateHoldLinkAsync --------------------------------------------------------------

    [Fact]
    public async Task CreateHoldLink_CopiesCentralAppearanceOntoPeripheral()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        Guid centreId;
        Guid peripheralId;
        await using (var db = h.CreateContext())
        {
            var centre = NewPanel(h.WallId, 0, 0);
            var far = NewPanel(h.WallId, 2, 0);
            db.WallPanels.AddRange(centre, far);

            var centreHold = NewHold(h.WallId, centre.Id, "red");
            centreHold.Material = HoldMaterial.PU;
            centreHold.Category = HoldCategory.Foot;
            centreHold.HandType = HoldHandType.Pinch;
            var peripheralHold = NewHold(h.WallId, far.Id, "blue");
            db.Holds.AddRange(centreHold, peripheralHold);
            await db.SaveChangesAsync();
            centreId = centreHold.Id;
            peripheralId = peripheralHold.Id;
        }

        var service = NewPanelService(h);
        await service.CreateHoldLinkAsync(h.WallId, peripheralId, centreId);

        await using var check = h.CreateContext();
        var peripheral = await check.Holds.FirstAsync(x => x.Id == peripheralId);
        Assert.Equal("red", peripheral.Color);
        Assert.Equal(HoldMaterial.PU, peripheral.Material);
        Assert.Equal(HoldCategory.Foot, peripheral.Category);
        Assert.Equal(HoldHandType.Pinch, peripheral.HandType);
    }

    // ---- UpdateHoldAsync ------------------------------------------------------------------

    [Fact]
    public async Task UpdateHold_PropagatesEditedColourToLinkedTwins()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        Guid centreId;
        Guid rightId;
        double rx;
        double ry;
        await using (var db = h.CreateContext())
        {
            var centre = NewPanel(h.WallId, 0, 0);
            var right = NewPanel(h.WallId, 1, 0);
            db.WallPanels.AddRange(centre, right);

            var centreHold = NewHold(h.WallId, centre.Id, "red");
            var rightHold = NewHold(h.WallId, right.Id, "blue");
            db.Holds.AddRange(centreHold, rightHold);
            db.HoldLinks.Add(NewLink(h.WallId, centreHold.Id, rightHold.Id));
            await db.SaveChangesAsync();
            centreId = centreHold.Id;
            rightId = rightHold.Id;
            rx = rightHold.X;
            ry = rightHold.Y;
        }

        // Edit the PERIPHERAL hold: the edited hold is the source regardless of centrality.
        await h.WallService.UpdateHoldAsync(rightId, rx, ry, 0.02, color: "green", category: HoldCategory.Hand);

        await using var check = h.CreateContext();
        Assert.Equal("green", (await check.Holds.FirstAsync(x => x.Id == rightId)).Color);
        Assert.Equal("green", (await check.Holds.FirstAsync(x => x.Id == centreId)).Color);
    }

    // ---- Fixtures -------------------------------------------------------------------------

    private sealed record Appearance(string? Color, HoldMaterial? Material, HoldCategory Category, HoldHandType? HandType);

    private static WallPanelService NewPanelService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    // centre(0,0) - right(1,0) - right2(2,0) with two links forming a transitive chain.
    private static async Task<(Guid Centre, Guid Right, Guid Right2)> SeedChainAsync(
        WallTestHarness h,
        Appearance centre,
        Appearance right,
        Appearance? right2)
    {
        await using var db = h.CreateContext();

        var p0 = NewPanel(h.WallId, 0, 0);
        var p1 = NewPanel(h.WallId, 1, 0);
        var p2 = NewPanel(h.WallId, 2, 0);
        db.WallPanels.AddRange(p0, p1, p2);

        var centreHold = Apply(NewHold(h.WallId, p0.Id, centre.Color), centre);
        var rightHold = Apply(NewHold(h.WallId, p1.Id, right.Color), right);
        db.Holds.AddRange(centreHold, rightHold);

        var right2Appearance = right2 ?? new Appearance("green", HoldMaterial.PE, HoldCategory.Foot, HoldHandType.Sloper);
        var right2Hold = Apply(NewHold(h.WallId, p2.Id, right2Appearance.Color), right2Appearance);
        db.Holds.Add(right2Hold);

        db.HoldLinks.Add(NewLink(h.WallId, centreHold.Id, rightHold.Id));
        db.HoldLinks.Add(NewLink(h.WallId, rightHold.Id, right2Hold.Id));

        await db.SaveChangesAsync();
        return (centreHold.Id, rightHold.Id, right2Hold.Id);
    }

    private static Hold Apply(Hold hold, Appearance a)
    {
        hold.Color = a.Color;
        hold.Material = a.Material;
        hold.Category = a.Category;
        hold.HandType = a.HandType;
        return hold;
    }

    private static async Task AssertAllEqualAsync(
        WallTestHarness h,
        string? color,
        HoldMaterial? material,
        HoldCategory category,
        HoldHandType? handType,
        params Guid[] ids)
    {
        await using var db = h.CreateContext();
        foreach (var id in ids)
        {
            var hold = await db.Holds.FirstAsync(x => x.Id == id);
            Assert.Equal(color, hold.Color);
            Assert.Equal(material, hold.Material);
            Assert.Equal(category, hold.Category);
            Assert.Equal(handType, hold.HandType);
        }
    }

    private static async Task<List<(string?, HoldMaterial?, HoldCategory, HoldHandType?)>> SnapshotAsync(
        WallTestHarness h,
        params Guid[] ids)
    {
        await using var db = h.CreateContext();
        var snapshot = new List<(string?, HoldMaterial?, HoldCategory, HoldHandType?)>();
        foreach (var id in ids)
        {
            var hold = await db.Holds.FirstAsync(x => x.Id == id);
            snapshot.Add((hold.Color, hold.Material, hold.Category, hold.HandType));
        }

        return snapshot;
    }

    private static WallPanel NewPanel(Guid wallId, int col, int row) =>
        new()
        {
            WallId = wallId,
            Col = col,
            Row = row,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            Generation = 0,
        };

    private static Hold NewHold(Guid wallId, Guid panelId, string? color) =>
        new()
        {
            WallId = wallId,
            WallPanelId = panelId,
            X = 0.5,
            Y = 0.5,
            Radius = 0.02,
            Color = color,
            Generation = 0,
        };

    private static HoldLink NewLink(Guid wallId, Guid holdAId, Guid holdBId) =>
        new()
        {
            WallId = wallId,
            HoldAId = holdAId,
            HoldBId = holdBId,
            Kind = HoldLinkKind.Same,
        };
}
