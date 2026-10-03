using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>The wall-admin clean-up of existing shapes: one journal batch, manual shapes untouched, exact revert.</summary>
public sealed class HoldShapeCleanupServiceTests : IDisposable
{
    private readonly WallTestHarness harness = new();
    private readonly ChangeJournal journal = new();
    private Guid spiky;
    private Guid overlapping;
    private Guid manualSpiky;
    private List<ShapePoint> spikyShape = [];

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task PreviewWritesNothing()
    {
        await SeedAsync();

        var preview = await Service().PreviewAsync(harness.WallId);

        Assert.Equal(2, preview.AutoShapes);
        Assert.Equal(1, preview.LockedHolds);
        Assert.True(preview.Smoothed + preview.Clipped + preview.BackToCircle >= 2);
        Assert.Null(preview.BatchId);
        Assert.Equal(spikyShape.Count, (await LoadAsync())[spiky].ShapePoints!.Count);
    }

    [Fact]
    public async Task ApplySmoothsAndSeparatesAutoShapesAndNeverTouchesTheManualOne()
    {
        await SeedAsync();
        var manualBefore = (await LoadAsync())[manualSpiky].ShapePoints!.Select(p => (p.Dx, p.Dy)).ToList();

        var result = await Service().ApplyAsync(harness.WallId);

        Assert.NotNull(result.BatchId);
        var holds = await LoadAsync();
        Assert.Equal(manualBefore, holds[manualSpiky].ShapePoints!.Select(p => (p.Dx, p.Dy)).ToList());
        Assert.Equal(HoldOutlineSource.Manual, holds[manualSpiky].OutlineSource);
        Assert.True(HoldShapeSmoother.IsSmooth(holds[spiky].ShapePoints!));
        Assert.Empty(HoldShapeCleanup.Plan(holds.Values.Where(h => h.WallPanelId != null).ToList()));
    }

    [Fact]
    public async Task ASecondRunFindsNothingToDo()
    {
        await SeedAsync();
        await Service().ApplyAsync(harness.WallId);

        var second = await Service().ApplyAsync(harness.WallId);

        Assert.Null(second.BatchId);
        Assert.Equal(0, second.Smoothed + second.Clipped + second.BackToCircle);
    }

    [Fact]
    public async Task RevertRestoresTheExactPreviousShapes()
    {
        await SeedAsync();
        var before = await LoadAsync();
        var result = await Service().ApplyAsync(harness.WallId);

        var reverted = await Service().RevertAsync(harness.WallId, result.BatchId!.Value);

        Assert.True(reverted.Reverted, string.Join(";", reverted.Conflicts.Select(c => c.ToString())) + reverted.Error);
        var after = await LoadAsync();
        Assert.Equal(Key(before[spiky]), Key(after[spiky]));
        Assert.Equal(Key(before[overlapping]), Key(after[overlapping]));
    }

    [Fact]
    public async Task RevertIsRefusedForAnUnknownBatch()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<UserFacingException>(() => Service().RevertAsync(harness.WallId, Guid.NewGuid()));
    }

    private static string Key(Hold h) =>
        $"{h.Radius:R}|{h.OutlineSource}|{string.Join(",", (h.ShapePoints ?? []).Select(p => $"{p.Dx:R}/{p.Dy:R}"))}";

    private HoldShapeCleanupService Service()
    {
        var factory = new JournalingFactory(((TestDbContextFactory)harness.DbContextFactory).ConnectionString, journal);
        return new HoldShapeCleanupService(
            factory,
            harness.CurrentUser,
            journal,
            new ChangeJournalReverter(factory, journal),
            NullLogger<HoldShapeCleanupService>.Instance);
    }

    private async Task<Dictionary<Guid, Hold>> LoadAsync()
    {
        await using var db = harness.CreateContext();
        return await db.Holds.AsNoTracking().ToDictionaryAsync(h => h.Id);
    }

    private async Task SeedAsync()
    {
        await harness.SeedWallAsync(holdCount: 0);
        var panelId = await EnrichmentScenario.AddPanelAsync(harness, livePhoto: [1, 2, 3]);
        spikyShape = HoldShapeSmootherTests.Blob(0.03, 0.03, 24).ToList();
        spikyShape.Insert(4, new ShapePoint { Dx = 0.09, Dy = 0 });
        await using var db = harness.CreateContext();
        spiky = Add(db, panelId, 0.30, spikyShape, HoldOutlineSource.AutoContour, auto: true);
        overlapping = Add(db, panelId, 0.37, HoldShapeSmootherTests.Blob(0.05, 0.05, 24), HoldOutlineSource.AutoContour, auto: true);
        manualSpiky = Add(db, panelId, 0.80, spikyShape, HoldOutlineSource.Manual, auto: false);
        await db.SaveChangesAsync();
    }

    private Guid Add(BlocwerkDbContext db, Guid panelId, double x, List<ShapePoint> shape, HoldOutlineSource source, bool auto)
    {
        var hold = EnrichmentFakes.AutoHold(harness.WallId, x, 0.5);
        hold.WallPanelId = panelId;
        hold.Generation = 1;
        hold.Radius = 0.05;
        hold.IsAutoDetected = auto;
        hold.ShapePoints = shape.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
        hold.OutlineSource = source;
        db.Holds.Add(hold);
        return hold.Id;
    }

    private sealed class JournalingFactory(string connectionString, ChangeJournal journal) : IDbContextFactory<BlocwerkDbContext>
    {
        public BlocwerkDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<BlocwerkDbContext>()
                .UseSqlite(connectionString)
                .AddInterceptors(new ChangeJournalInterceptor(journal))
                .Options;
            return new SqliteBlocwerkDbContext(options) { CurrentUserId = Guid.Empty };
        }
    }
}
