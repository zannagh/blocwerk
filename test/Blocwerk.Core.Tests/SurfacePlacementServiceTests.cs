// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Place holds on the 3D surface" end to end on <see cref="HoldPlacementScenario"/>'s wall with a <see cref="SurfaceScene"/>
/// photo-real view and a delivered panel registration (c0 nearly head-on, c1 steep). Only the holds' 3D fields may change.
/// </summary>
public sealed class SurfacePlacementServiceTests : IDisposable
{
    private static readonly SolvedCamera C0 = SurfaceScene.LookAt("c0", [900, -3000, 1100], [800, 0, 1200]);
    private static readonly SolvedCamera C1 = SurfaceScene.LookAt("c1", [2600, -2200, 400], [800, 0, 1200]);
    private readonly string storeDir = Path.Combine(Path.GetTempPath(), "blocwerk-surface-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(storeDir))
        {
            Directory.Delete(storeDir, recursive: true);
        }
    }

    [Fact]
    public async Task Place_PutsLinkedCopiesOnOneSurfacePosition_AndLeavesPanelGeometryLinksAndBouldersByteIdentical()
    {
        using var h = new WallTestHarness();
        var (s, service, onC0, onC1, alone) = await SeedAsync(h);
        var before = await HoldOutlierPlacementTests.NonThreeDSnapshotAsync(h);

        var result = await service.PlaceOnSurfaceAsync(h.WallId);

        Assert.Equal(before, await HoldOutlierPlacementTests.NonThreeDSnapshotAsync(h));
        var holds = await s.LoadHoldsAsync();
        Assert.Equal(3, result.Placed);
        Assert.Equal((holds[onC0].PlaneAMm, holds[onC0].PlaneBMm), (holds[onC1].PlaneAMm, holds[onC1].PlaneBMm));
        Assert.Equal(800, holds[onC0].PlaneAMm!.Value, 4.0);
        Assert.Equal(1100, holds[alone].PlaneAMm!.Value, 4.0);
        Assert.All(new[] { onC0, onC1, alone }, id => Assert.Equal(HoldMetric.Surface, holds[id].MetricSource));
        Assert.All(new[] { onC0, onC1 }, id => Assert.NotNull(holds[id].FootprintMm));
        s.Queue.Received(1).Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Count() == 3));
    }

    [Fact]
    public async Task Preview_CountsWithoutWriting_AndRevert_RestoresExactly()
    {
        using var h = new WallTestHarness();
        var (s, service, _, _, _) = await SeedAsync(h);
        var before = await s.LoadHoldsAsync();

        var preview = await service.PreviewSurfaceAsync(h.WallId);

        Assert.Equal((3, 2, 1, 1, 1), (preview.ToPlace, preview.OwnView, preview.SharedView, preview.Missed, preview.Pairs));
        Assert.Equivalent(before, await s.LoadHoldsAsync());
        var run = await service.PlaceOnSurfaceAsync(h.WallId);
        var revert = await service.RevertAsync(h.WallId, run.RunId);
        Assert.Equal(run.Placed, revert.Reverted);
        Assert.Equivalent(before, await s.LoadHoldsAsync());
    }

    [Fact]
    public async Task APoseOfAnEarlierPhotoRevision_IsNotUsed()
    {
        using var h = new WallTestHarness();
        var (s, service, _, _, _) = await SeedAsync(h);
        await using (var db = h.CreateContext())
        {
            (await db.WallPanels.SingleAsync(p => p.Id == s.PanelC1)).PhotoRevision = 1;
            await db.SaveChangesAsync();
        }

        var preview = await service.PreviewSurfaceAsync(h.WallId);

        Assert.Equal((1, 1, 0), (preview.Checked, preview.OwnView, preview.Pairs));
    }

    [Fact]
    public async Task NonAdmin_IsRefused()
    {
        using var h = new WallTestHarness();
        var (_, service, _, _, _) = await SeedAsync(h);
        h.ActingUser = await h.AddMemberAsync("climber@test", WallRole.Moderator);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewSurfaceAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlaceOnSurfaceAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RegisterPanelsAsync(h.WallId));
    }

    /// <summary>
    /// A hold at base (800, 1200) seen by both photos (linked; c0's copy on a boulder), one at (1100, 900) only in c1, one in c1
    /// where the view has no surface. All placed wrong (plane casts / a stray), all with outlines.
    /// </summary>
    private async Task<(HoldPlacementScenario S, HoldTexturePlacementService Service, Guid OnC0, Guid OnC1, Guid Alone)> SeedAsync(WallTestHarness h)
    {
        var s = await HoldPlacementScenario.CreateAsync(h);
        var onC0 = await AddAsync(s, SurfaceScene.HoldSeenBy(C0, s.PanelC0, 800, 1200), s.PanelC0);
        var onC1 = await AddAsync(s, SurfaceScene.HoldSeenBy(C1, s.PanelC1, 800, 1200), s.PanelC1);
        var alone = await AddAsync(s, SurfaceScene.HoldSeenBy(C1, s.PanelC1, 1100, 900), s.PanelC1);
        await AddAsync(s, SurfaceScene.HoldSeenBy(C1, s.PanelC1, 1700, 2400), s.PanelC1);
        s.Files.ReadAsync("s.spz", Arg.Any<CancellationToken>())
            .Returns(SurfaceScene.Spz(SurfaceScene.Points(500, 1400, 600, 1500, (800, 1200), (1100, 900))));

        var files = new FileSystemCaptureFileStore(new Configuration.BlocwerkSettings { WallImage = { StoragePath = storeDir } });
        var queue = new GpuJobQueue(h.RootContextFactory, files, new WallCaptureQueue(), new GpuRunnerOptions(), new GpuJobSignal(), NullLogger<GpuJobQueue>.Instance);
        await using var db = h.CreateContext();
        var capture = new WallCapture { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = WallCaptureStatus.Succeeded, Stage = "Done", GeometryModelId = s.ModelId };
        db.WallCaptures.Add(capture);
        db.WallGeometrySplats.Add(new WallGeometrySplat { GeometryModelId = s.ModelId, StoredPath = "s.spz", SizeBytes = 1, FrameJson = SurfaceScene.FrameJson });
        db.GpuJobs.Add(new GpuJob
        {
            WallId = h.WallId, CaptureId = capture.Id, GeometryModelId = s.ModelId, Kind = GpuJobKind.RegisterPanels, Status = GpuJobStatus.Succeeded,
            BundlePath = "b.zip", BundleSha256 = "x", PreparedPath = "b.zip",
            ResultPath = await files.SaveAsync(Poses((s.PanelC0, C0), (s.PanelC1, C1)), ".json", CancellationToken.None),
        });
        var boulder = new Boulder { WallId = h.WallId, Name = "Problem", CreatedByUserId = h.Owner.Id };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = onC0, Type = HoldType.Start });
        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = onC0, HoldBId = onC1, Kind = HoldLinkKind.Same });
        await db.SaveChangesAsync();
        var service = new HoldTexturePlacementService(
            h.DbContextFactory, h.CurrentUser, NullLogger<HoldTexturePlacementService>.Instance, s.Matcher, s.Files, s.Queue, gpuJobs: queue);
        return (s, service, onC0, onC1, alone);
    }

    private static Task<Guid> AddAsync(HoldPlacementScenario s, Hold seen, Guid panel) => s.AddHoldAsync(seen.X, seen.Y, panel, hold =>
    {
        hold.ShapePoints = seen.ShapePoints;
        (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm, hold.MetricSource) = ("0", 1500, 2500, HoldMetric.MultiMarker);
        (hold.WidthMm, hold.HeightMm, hold.AreaMm2, hold.ProtrusionMm) = (40, 30, 900, "{\"old\":true}");
    });

    /// <summary>A registration result as the runner uploads it.</summary>
    private static byte[] Poses(params (Guid Panel, SolvedCamera Camera)[] panels) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1,
        panels = panels.ToDictionary(
            p => RunnerPanelsBundle.Name(p.Panel, 0),
            p => new
            {
                width = p.Camera.Width, height = p.Camera.Height, @params = new[] { p.Camera.K[0], p.Camera.K[2], p.Camera.K[5], 0, 0 },
                R = p.Camera.R, t = p.Camera.T, observations = 5000, rmsPx = 2.0, medianPx = 1.5,
            }),
    });
}
