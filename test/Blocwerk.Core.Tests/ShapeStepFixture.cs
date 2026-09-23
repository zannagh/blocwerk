// <copyright file="ShapeStepFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Plumbing for the wall-update shape step suites: a deterministic fake outliner, the runner and service
/// over one <see cref="WallTestHarness"/>, and a staged update with holds on its centre panel.
/// </summary>
internal sealed class ShapeStepFixture
{
    public ShapeStepFixture(WallTestHarness h, IKioskContext? kiosk = null)
    {
        H = h;
        Runner = new WallShapeRecognitionRunner(
            h.RootContextFactory, new BlocwerkSettings(), NullLogger<WallShapeRecognitionRunner>.Instance, new FakeOutliner());
        Service = new WallUpdateShapeService(
            h.DbContextFactory, h.CurrentUser, Runner, NullLogger<WallUpdateShapeService>.Instance, kiosk);
        BigUpdate = WallUpdateSessionFixture.BigUpdate(h);
    }

    public WallTestHarness H { get; }

    public WallShapeRecognitionRunner Runner { get; }

    public WallUpdateShapeService Service { get; }

    public WallBigUpdateService BigUpdate { get; }

    public Guid SessionId { get; private set; }

    public Guid PanelId { get; private set; }

    /// <summary>A wall with no old holds, a staged centre, and the given staged holds on it.</summary>
    public async Task<List<Guid>> StageAsync(params Hold[] staged)
    {
        await H.SeedWallAsync(holdCount: 0);
        WallUpdateSessionFixture.NoDetections(H);
        await BigUpdate.StageAsync(H.WallId, WallUpdateSessionFixture.CentrePhoto());
        PanelId = await WallUpdateSessionFixture.PanelIdAsync(H, 0, 0);
        await using var db = H.CreateContext();
        SessionId = db.WallUpdateSessions.Single(s => s.Status == WallUpdateSessionStatus.Open).Id;
        foreach (var hold in staged)
        {
            hold.WallId = H.WallId;
            hold.WallPanelId = PanelId;
            hold.Generation = 1;
            db.Holds.Add(hold);
        }

        await db.SaveChangesAsync();
        return staged.Select(s => s.Id).ToList();
    }

    /// <summary>Starts a run and waits for it to end.</summary>
    public async Task<ShapeRecognitionStatusInfo> RecogniseAsync(ShapeRecognitionOptions? options = null)
    {
        await Service.StartRecognitionAsync(H.WallId, options ?? new ShapeRecognitionOptions(), SessionId);
        await Runner.WhenIdleAsync(SessionId);
        return await Service.GetStatusAsync(H.WallId);
    }

    /// <summary>Promotes, keeping every staged hold as a new hold.</summary>
    public async Task PromoteAsync(IEnumerable<Guid> keep)
    {
        await BigUpdate.PromoteAsync(H.WallId, new BigUpdateConfirmation([], keep.ToList(), [], []), SessionId);
    }

    public static Hold AutoHold(double x, List<ShapePoint>? shape = null, HoldOutlineSource? source = null) => new()
    {
        X = x,
        Y = 0.5,
        Radius = 0.02,
        IsAutoDetected = true,
        ShapePoints = shape,
        OutlineSource = source,
    };

    public static List<ShapePoint> Triangle(double r) =>
        [new() { Dx = 0, Dy = -r }, new() { Dx = r, Dy = r }, new() { Dx = -r, Dy = r }];

    /// <summary>
    /// Deterministic outliner: a hold left of x = 0.3 finds nothing (circle fallback, 0.1); anything else
    /// gets a square outline twice its seed radius wide with a confidence rising with x.
    /// </summary>
    private sealed class FakeOutliner : IHoldOutlineService
    {
        public IHoldOutlineSession OpenSession(byte[] encodedImage) => new Session();

        private sealed class Session : IHoldOutlineSession
        {
            public int ImageWidth => 1000;

            public int ImageHeight => 1000;

            public HoldOutlineResult Outline(HoldSeed seed)
            {
                var r = seed.Radius;
                var fallback = seed.X < 0.3;
                List<ShapePoint>? shape = fallback
                    ? null
                    : [new() { Dx = -r, Dy = -r }, new() { Dx = r, Dy = -r }, new() { Dx = r, Dy = r }, new() { Dx = -r, Dy = r }];
                var polygon = (shape ?? Triangle(r)).Select(p => new NormalizedPoint(seed.X + p.Dx, seed.Y + p.Dy)).ToList();
                return new HoldOutlineResult(
                    polygon,
                    seed.X,
                    seed.Y,
                    shape,
                    4 * r * r * 1e6,
                    new HoldOutlineBounds(seed.X - r, seed.Y - r, 2 * r, 2 * r),
                    fallback ? 0.1 : Math.Round(0.3 + seed.X / 2, 3),
                    fallback ? HoldOutlineMethod.CircleFallback : HoldOutlineMethod.Contour,
                    new HoldFingerprint());
            }

            public void Dispose()
            {
            }
        }
    }
}
