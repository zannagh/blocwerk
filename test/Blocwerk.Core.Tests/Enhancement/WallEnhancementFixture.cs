// <copyright file="WallEnhancementFixture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>
/// A wall with one live panel and journalling services over the same database (SQLite or Postgres), plus the
/// enhancement job, runner and service built from whatever steps a test hands in.
/// </summary>
public sealed class WallEnhancementFixture : IDisposable
{
    private readonly ChangeJournal journal = new();

    private WallEnhancementFixture(WallTestHarness harness)
    {
        Harness = harness;
        var connection = harness.DbContextFactory.ConnectionString;
        Journalling = new EnhancementJournallingFactory(connection, journal);
    }

    public WallTestHarness Harness { get; }

    public IDbContextFactory<BlocwerkDbContext> Journalling { get; }

    public ChangeJournal Journal => journal;

    public Guid WallId => Harness.WallId;

    public Guid PanelId { get; private set; }

    public IHoldDuplicateService Duplicates { get; set; } = Substitute.For<IHoldDuplicateService>();

    public IHoldLinkSuggestionService Links { get; } = Substitute.For<IHoldLinkSuggestionService>();

    public IHoldProposalService Proposals { get; } = Substitute.For<IHoldProposalService>();

    public IHoldRefinementQueue RefinementQueue { get; } = Substitute.For<IHoldRefinementQueue>();

    public IKioskContext? Kiosk { get; set; }

    public IWallEditPresence? Presence { get; set; }

    public WallEnhancementRunner? Runner { get; private set; }

    public static async Task<WallEnhancementFixture> CreateAsync(PostgresTestDatabase? postgres = null)
    {
        var harness = new WallTestHarness(postgres);
        await harness.SeedWallAsync(holdCount: 0);
        var fixture = new WallEnhancementFixture(harness);
        await using var db = harness.CreateContext();
        var panel = new WallPanel { WallId = harness.WallId, Col = 0, Row = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg", Generation = 0 };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        fixture.PanelId = panel.Id;
        return fixture;
    }

    public async Task<Guid> AddHoldAsync(double x, double y, Action<Hold>? configure = null)
    {
        await using var db = Harness.CreateContext();
        var hold = new Hold { WallId = WallId, WallPanelId = PanelId, X = x, Y = y, Radius = 0.03, Generation = 0, IsAutoDetected = true };
        configure?.Invoke(hold);
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    public async Task SetPanelPhotoAsync(byte[] photo)
    {
        await using var db = Harness.CreateContext();
        var panel = await db.WallPanels.FirstAsync(p => p.Id == PanelId);
        panel.Photo = photo;
        panel.PhotoContentType = "image/png";
        await db.SaveChangesAsync();
    }

    public async Task<Hold> GetHoldAsync(Guid id)
    {
        await using var db = Harness.CreateContext();
        return await db.Holds.AsNoTracking().FirstAsync(h => h.Id == id);
    }

    public async Task EditHoldAsync(Guid id, Action<Hold> change)
    {
        await using var db = Harness.CreateContext();
        change(await db.Holds.FirstAsync(h => h.Id == id));
        await db.SaveChangesAsync();
    }

    public async Task BumpGenerationAsync()
    {
        await using var db = Harness.CreateContext();
        var wall = await db.Walls.FirstAsync(w => w.Id == WallId);
        wall.CurrentGeneration++;
        await db.SaveChangesAsync();
    }

    public async Task<List<WallEnhancementItem>> ItemsAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.WallEnhancementItems.AsNoTracking().ToListAsync();
    }

    public async Task<WallEnhancementRun> RunAsync()
    {
        await using var db = Harness.CreateContext();
        var runs = await db.WallEnhancementRuns.AsNoTracking().Where(r => r.WallId == WallId).ToListAsync();
        return runs.MaxBy(r => r.Generation)!;
    }

    public async Task<List<ChangeJournalBatch>> BatchesAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.ChangeJournalBatches.AsNoTracking().ToListAsync();
    }

    /// <summary>The job, runner and service over the given steps (built once per fixture).</summary>
    public WallEnhancementService Service(params IWallEnhancementStep[] steps)
    {
        Runner ??= new WallEnhancementRunner(Job(steps));
        return new WallEnhancementService(
            Journalling, Harness.CurrentUser, Runner, journal, new ChangeJournalReverter(Journalling, journal),
            Duplicates, Links, Proposals, NullLogger<WallEnhancementService>.Instance, Kiosk, Presence, RefinementQueue);
    }

    public WallEnhancementJob Job(IReadOnlyList<IWallEnhancementStep> steps) => new(
        Journalling,
        new EnhancementTestActors(this),
        steps,
        journal,
        NullLogger<WallEnhancementJob>.Instance,
        editPresence: Presence,
        pulse: Pulses) { EditorPoll = TimeSpan.FromMilliseconds(20), StepTimeout = StepTimeout, HeartbeatInterval = HeartbeatInterval, StopGrace = StopGrace, ObserveLimit = ObserveLimit, Clock = Clock ?? TimeProvider.System };

    /// <summary>The job's pulses (the card's progress), unthrottled.</summary>
    public Pulse.RecordingWallPulse Pulses { get; } = new();

    /// <summary>A hand-fired clock for the step watchdog, or null for the real one.</summary>
    public ManualTimeProvider? Clock { get; set; }

    /// <summary>How long the job waits for a stopped step before it lets go.</summary>
    public TimeSpan ObserveLimit { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a stopped step gets to return.</summary>
    public TimeSpan StopGrace { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The job's heartbeat interval.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The job's step watchdog (long by default).</summary>
    public TimeSpan StepTimeout { get; set; } = TimeSpan.FromHours(4);

    /// <summary>Starts the run and waits for its steps.</summary>
    public async Task<WallEnhancementView> StartAndWaitAsync(WallEnhancementService service)
    {
        var started = await service.StartAsync(WallId);
        await Runner!.WhenIdleAsync(started.RunId!.Value);
        return await service.GetAsync(WallId);
    }

    /// <summary>The real wall services over the journalling factory, acting as the harness's user.</summary>
    public WallEnhancementActors RealActors()
    {
        var reverter = new ChangeJournalReverter(Journalling, journal);
        return new WallEnhancementActors(
            new HoldOutlineUpgradeService(Journalling, Harness.CurrentUser, new Configuration.BlocwerkSettings(), NullLogger<HoldOutlineUpgradeService>.Instance),
            new HoldStackService(Journalling, Harness.CurrentUser, NullLogger<HoldStackService>.Instance, journal),
            new HoldShapeCleanupService(Journalling, Harness.CurrentUser, journal, reverter, NullLogger<HoldShapeCleanupService>.Instance),
            new HoldColorProposalService(Journalling, Harness.CurrentUser, new HoldColorProposer(), NullLogger<HoldColorProposalService>.Instance, journal),
            new HoldDuplicateService(Journalling, Harness.CurrentUser, journal, reverter, NullLogger<HoldDuplicateService>.Instance),
            Links,
            Proposals);
    }

    public bool UseRealActors { get; set; }

    public void Dispose()
    {
        Runner?.Dispose();
        Harness.Dispose();
    }
}
