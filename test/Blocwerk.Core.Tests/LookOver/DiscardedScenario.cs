// <copyright file="DiscardedScenario.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.HoldColors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// A panel update on the wall of <see cref="HoldColorScenario"/> whose triage discarded detections: one hold is kept
/// on the blue spot, and the rest of the staged detections are removed at the promote, each with or without a reason.
/// </summary>
internal sealed class DiscardedScenario : IDisposable
{
    private DiscardedScenario(HoldColorScenario colors, Guid panelId, Guid keptId)
    {
        (Colors, PanelId, KeptId) = (colors, panelId, keptId);
    }

    public HoldColorScenario Colors { get; }

    public WallTestHarness Harness => Colors.Harness;

    public Guid WallId => Colors.WallId;

    /// <summary>The panel the update brought live.</summary>
    public Guid PanelId { get; }

    /// <summary>The one new hold the update kept, on the blue spot.</summary>
    public Guid KeptId { get; }

    /// <summary>
    /// The kept hold's radius: about the size of a discarded detection, so a detection on its spot is a second detection
    /// of it. A clearly smaller detection would be a hold ON it (<see cref="Blocwerk.Core.HoldStacks.HoldStacking"/>).
    /// </summary>
    public const double KeptRadius = 0.055;

    /// <summary>The octagon every discarded detection of the scenario was detected with.</summary>
    public static List<ShapePoint> Outline => ShapePoint.DefaultOctagon(0.05);

    /// <summary>Stages a panel with a kept hold and <paramref name="discarded"/>, and applies the update.</summary>
    /// <param name="discarded">Per removed detection: its spot, the detector's confidence and the triage's reason (null: a person removed it).</param>
    /// <returns>The scenario after the promote.</returns>
    public static async Task<DiscardedScenario> ApplyAsync(params ((double X, double Y) Spot, double Confidence, NewHoldDiscardReason? Reason)[] discarded)
    {
        var colors = await HoldColorScenario.CreateAsync();
        var panel = await colors.AddPanelAsync(0, generation: 1, staged: true);
        var kept = await colors.AddHoldAsync(panel, HoldColorScenario.BlueSpot, generation: 1, h => h.Radius = KeptRadius);
        var removed = new List<Guid>();
        foreach (var (spot, confidence, reason) in discarded)
        {
            removed.Add(await colors.AddHoldAsync(
                panel, spot, generation: 1, h => (h.Confidence, h.DiscardReason, h.ShapePoints, h.NeedsReview) = (confidence, reason?.ToString(), Outline, true)));
        }

        await colors.BigUpdate(new HoldColorProposer()).PromoteAsync(colors.WallId, new BigUpdateConfirmation([], [kept], removed, []));
        return new DiscardedScenario(colors, panel, kept);
    }

    /// <summary>The look-over service with a journal and the real colour proposer, acting as the owner.</summary>
    public WallLookOverService LookOver()
    {
        var journal = new ChangeJournal();
        var factory = new JournalledFactory(Harness.DbContextFactory.ConnectionString, journal);
        var duplicates = new HoldDuplicateService(
            factory, Harness.CurrentUser, journal, new ChangeJournalReverter(factory, journal), NullLogger<HoldDuplicateService>.Instance);
        return new WallLookOverService(
            factory, Harness.CurrentUser, Harness.WallService, duplicates, Substitute.For<ICaptureFileStore>(), NullLogger<WallLookOverService>.Instance,
            null, journal, new HoldColorProposer());
    }

    /// <summary>The wall's discarded-detection findings, the surest first.</summary>
    public async Task<List<WallLookOverFinding>> FindingsAsync()
    {
        await using var db = Harness.CreateContext();
        return (await db.WallLookOverFindings.AsNoTracking()
                .Where(f => f.WallId == WallId && f.Kind == LookOverFindingKind.DiscardedDetection)
                .ToListAsync())
            .OrderByDescending(f => f.Confidence)
            .ToList();
    }

    /// <summary>The wall's holds of the generation the update produced.</summary>
    public async Task<List<Hold>> HoldsAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.Holds.AsNoTracking().Where(h => h.WallId == WallId && h.Generation == 1).ToListAsync();
    }

    public void Dispose() => Colors.Dispose();
}
