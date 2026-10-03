// <copyright file="RefreshScenario.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Update panels + 3D" over a <see cref="WallTestHarness"/>: the real capture service (fake compute, temp file
/// store), the real big update and session services (scripted detection, no matcher), a substituted hold
/// placement and an alignment that says every photo shows the centre panel frontally. The worker is driven by
/// hand: <see cref="RunQueuedAsync"/> processes what the page enqueued.
/// </summary>
internal sealed class RefreshScenario : IDisposable
{
    private readonly WallTestHarness harness;

    public RefreshScenario(WallTestHarness harness)
    {
        this.harness = harness;
        Capture = new CaptureScenario(harness);
        WallUpdateSessionFixture.NoDetections(harness);
        Placement.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new HoldPlacementStatus(true, false, null));
        var alignment = Substitute.For<IImageAlignmentService>();
        alignment.AlignNormalizedAsync(Arg.Any<byte[]>(), Arg.Any<byte[]>())
            .Returns(new Homography([1, 0, 0, 0, 1, 0, 0, 0, 1], 200, 0.9));
        var actors = new DelegateRefreshActors(ActorsForAsync);
        Service = new WallRefreshService(
            harness.DbContextFactory, harness.CurrentUser, Capture.Service, Queue, actors, Capture.Files,
            NullLogger<WallRefreshService>.Instance, Capture.Options);
        Processor = new WallRefreshProcessor(
            harness.RootContextFactory, actors, new PanelPhotoPicker(alignment), Capture.Files, NullLogger<WallRefreshProcessor>.Instance);
    }

    /// <summary>Wraps the services the worker gets (e.g. to make one step fail).</summary>
    public Func<WallRefreshActors, WallRefreshActors> Decorate { get; set; } = actors => actors;

    public CaptureScenario Capture { get; }

    public IHoldTexturePlacementService Placement { get; } = Substitute.For<IHoldTexturePlacementService>();

    public WallRefreshQueue Queue { get; } = new();

    public WallRefreshService Service { get; }

    public WallRefreshProcessor Processor { get; }

    /// <summary>A wall without markers: a live centre panel and <paramref name="holds"/> old holds on it.</summary>
    public async Task<List<Hold>> SeedWallAsync(int holds = 2)
    {
        var old = await harness.SeedWallAsync(holdCount: holds);
        await AddLivePanelAsync(old);
        return old;
    }

    /// <summary>A marker wall whose earlier capture finished (its declarations prefill the next one), with a live centre panel.</summary>
    public async Task SeedMarkerWallWithEarlierCaptureAsync()
    {
        var earlier = await Capture.StartCaptureAsync();
        await using (var db = harness.CreateContext())
        {
            var capture = await db.WallCaptures.FindAsync(earlier);
            capture!.Status = WallCaptureStatus.Succeeded;
            await db.SaveChangesAsync();
        }

        _ = await Capture.Queue.DequeueAsync(CancellationToken.None);
        await AddLivePanelAsync([]);
    }

    /// <summary>Opens a run and drops <paramref name="photos"/> photos into it.</summary>
    public async Task<Guid> DropPhotosAsync(int photos = 2)
    {
        var view = await Service.BeginAsync(harness.WallId);
        for (var i = 0; i < photos; i++)
        {
            await Service.AddPhotoAsync(view.Id, $"IMG_{i}.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(seed: 40 + i)), CancellationToken.None);
        }

        return view.Id;
    }

    /// <summary>Drop, sort, start with the proposed photos: the run then waits at the confirm screen.</summary>
    public async Task<WallRefreshView> PrepareAsync()
    {
        var id = await DropPhotosAsync();
        await Service.SortAsync(id);
        await RunQueuedAsync();
        var sorted = await CurrentAsync();
        await Service.StartAsync(id, sorted.Picks.Select(p => new PanelChoice(p.Col, p.Row, p.PhotoId)).ToList());
        await RunQueuedAsync();
        return await CurrentAsync();
    }

    public async Task<WallRefreshView> CurrentAsync() => (await Service.GetCurrentAsync(harness.WallId))!;

    public async Task RunQueuedAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var id = await Queue.DequeueAsync(cts.Token);
        await Processor.ProcessAsync(id, CancellationToken.None);
    }

    /// <summary>The services acting as <paramref name="userId"/>, as the app's actor factory builds them.</summary>
    public async Task<WallRefreshActors> ActorsForAsync(Guid userId)
    {
        User user;
        await using (var db = harness.CreateContext())
        {
            user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
        }

        var acting = new CaptureActingUser(user);
        var capture = new WallCaptureService(
            harness.DbContextFactory, acting, Capture.Files, Capture.Queue, new FakeComputeJobClientFactory(Capture.Client, Capture.SplatClient),
            NullLogger<WallCaptureService>.Instance, null, Capture.Detector, Capture.MarkerPlans, Capture.Video, Capture.Options,
            textureQueue: Capture.TextureQueue, resolveQueue: Capture.ResolveQueue);
        var matcher = Substitute.For<IHoldOverlapMatcher>();
        var panels = new WallPanelService(harness.DbContextFactory, acting, harness.HoldDetection, matcher, NullLogger<WallPanelService>.Instance);
        return Decorate(new WallRefreshActors(
            capture,
            new CapturePanelPhotoService(capture, panels, NullLogger<CapturePanelPhotoService>.Instance),
            new WallBigUpdateService(harness.DbContextFactory, acting, harness.HoldDetection, matcher, NullLogger<WallBigUpdateService>.Instance),
            new WallUpdateSessionService(harness.DbContextFactory, acting, NullLogger<WallUpdateSessionService>.Instance),
            Placement));
    }

    public void Dispose() => Capture.Dispose();

    private async Task AddLivePanelAsync(List<Hold> old)
    {
        await using var db = harness.CreateContext();
        var panel = new WallPanel { WallId = harness.WallId, Col = 0, Row = 0, Generation = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" };
        db.WallPanels.Add(panel);
        foreach (var hold in old)
        {
            db.Holds.Attach(hold);
            hold.WallPanelId = panel.Id;
        }

        await db.SaveChangesAsync();
    }
}
