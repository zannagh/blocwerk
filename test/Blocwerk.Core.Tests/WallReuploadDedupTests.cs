using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the re-upload dedup on <see cref="WallService.UploadPhotoAsync"/>: uploading a fresh photo
/// onto a wall whose center panel already carries a detection set must REPLACE that set, not layer a
/// second one on top of it. The replacement is boulder-safe — manual holds and any hold a boulder
/// references survive so no boulder is orphaned.
/// </summary>
public class WallReuploadDedupTests
{
    private static Task ConvergeAsync(WallTestHarness h) =>
        WallCenterPanelConvergence.RunIfNeededAsync(h.DbContextFactory, NullLogger.Instance);

    [Fact]
    public async Task Reupload_ReplacesLooseAutoHolds_ButKeepsManualAndBoulderReferencedHolds()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        // Converge seeds the (0,0) center panel the re-upload path recognises as "live".
        await ConvergeAsync(h);

        Guid centerPanelId;
        Guid referencedAutoId;
        Guid manualId;
        Guid looseAutoOneId;
        Guid looseAutoTwoId;
        Guid boulderId;
        await using (var seed = h.CreateContext())
        {
            var panel = await seed.WallPanels.SingleAsync(p => p.WallId == h.WallId && p.Col == 0 && p.Row == 0);
            centerPanelId = panel.Id;

            var referencedAuto = new Hold { WallId = h.WallId, WallPanelId = panel.Id, X = 0.1, Y = 0.1, Radius = 0.02, Generation = 0, IsAutoDetected = true };
            var manual = new Hold { WallId = h.WallId, WallPanelId = panel.Id, X = 0.2, Y = 0.2, Radius = 0.02, Generation = 0, IsAutoDetected = false };
            var looseAutoOne = new Hold { WallId = h.WallId, WallPanelId = panel.Id, X = 0.3, Y = 0.3, Radius = 0.02, Generation = 0, IsAutoDetected = true };
            var looseAutoTwo = new Hold { WallId = h.WallId, WallPanelId = panel.Id, X = 0.4, Y = 0.4, Radius = 0.02, Generation = 0, IsAutoDetected = true };
            seed.Holds.AddRange(referencedAuto, manual, looseAutoOne, looseAutoTwo);

            var boulder = new Boulder { WallId = h.WallId, Name = "Uses an auto hold", CreatedByUserId = h.Owner.Id };
            seed.Boulders.Add(boulder);
            seed.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = referencedAuto.Id });
            await seed.SaveChangesAsync();

            referencedAutoId = referencedAuto.Id;
            manualId = manual.Id;
            looseAutoOneId = looseAutoOne.Id;
            looseAutoTwoId = looseAutoTwo.Id;
            boulderId = boulder.Id;
        }

        // Re-upload with fresh bytes and two freshly-detected holds.
        var detected = new List<DetectedHold>
        {
            new(0.6, 0.6, 0.02, null, 0.9),
            new(0.7, 0.7, 0.02, null, 0.9),
        };
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(detected));

        await h.WallService.UploadPhotoAsync(h.WallId, [7, 7, 7], "image/png");

        await using var db = h.CreateContext();

        // The two unreferenced auto holds from the previous set are gone — replaced, not doubled.
        Assert.False(await db.Holds.AnyAsync(x => x.Id == looseAutoOneId));
        Assert.False(await db.Holds.AnyAsync(x => x.Id == looseAutoTwoId));

        // The manual hold and the boulder-referenced auto hold survive.
        Assert.True(await db.Holds.AnyAsync(x => x.Id == manualId));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == referencedAutoId));

        // The boulder keeps its hold link intact.
        Assert.True(await db.BoulderHolds.AnyAsync(bh => bh.BoulderId == boulderId && bh.HoldId == referencedAutoId));

        // The center panel now carries exactly the survivors plus the fresh detection set: no duplication.
        var onCenter = await db.Holds.Where(x => x.WallPanelId == centerPanelId).Select(x => x.Id).ToListAsync();
        Assert.Equal(4, onCenter.Count);
        Assert.Contains(manualId, onCenter);
        Assert.Contains(referencedAutoId, onCenter);
    }
}
