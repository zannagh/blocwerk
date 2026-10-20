// <copyright file="OffCentreNotSameTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Not the same hold" on an unsure-match card about a hold on a panel OTHER than the centre, applied the way the quick
/// flow's Apply does (<see cref="RefreshDecisions.LoadAsync"/> and its scope reconcile). The card showed the pair, so the
/// answer sticks through Apply exactly as on the centre: the old hold is carried blind, unconfirmed and marked, the
/// detection goes live as a new hold, and the look-over asks about the old hold. It used to be reset to the matcher's
/// twin at the promote, which left neither the answer nor a finding.
/// </summary>
public class OffCentreNotSameTests
{
    private const double SideX = 0.4;
    private const double SideY = 0.65;

    [Fact]
    public async Task NotTheSameHold_OnTheRightPanel_SticksThroughApply_AndLeavesAFinding()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true, neighbour: new LookNeighbour(SideX, SideY, OldHold: true));
        w.Probe.Set(SideX, SideY, 0.4);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        var oldId = w.NeighbourTargetId!.Value;
        var detectionId = w.NeighbourDetectionId!.Value;
        var card = (await w.Sessions.GetUpdateExceptionsAsync(w.WallId))
            .Single(c => c.Kind == UpdateExceptionKind.LowConfidenceMatch && c.OldHoldId == oldId);

        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card.Id, UpdateExceptionAnswer.NotSame, w.SessionId);

        var actors = new WallRefreshActors(null!, null!, w.Service, w.Sessions, null);
        var promotable = await RefreshDecisions.LoadAsync(w.WallId, actors, session, w.StagedByPanel);
        Assert.Equal(0, promotable.Resets);
        var decision = promotable.Scoped.Carryover.Single(d => d.OldHoldId == oldId);
        Assert.Equal((CarryKind.Carried, (Guid?)null, false), (decision.Kind, decision.NewHoldId, decision.Confirmed));

        await w.Service.PromoteAsync(w.WallId, promotable.Scoped with { ExpectedMovesVersion = promotable.Moves.Version }, w.SessionId);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == oldId);
        Assert.NotEqual(detectionId, link.NewHoldId);
        var successor = await db.Holds.SingleAsync(x => x.Id == link.NewHoldId);
        Assert.Equal((3, true), (successor.Generation, successor.NeedsReview));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == detectionId && x.Generation == 3));
        Assert.True(await db.WallLookOverFindings.AnyAsync(f => f.OldHoldId == oldId));
    }

    // The step-by-step review re-entered after the card: its restore and seed lay the answer back (no "reset" notice),
    // Continue saves it again, and Apply still promotes it — one successor, the detection a separate new hold.
    [Fact]
    public async Task NotTheSameHold_OnTheRightPanel_SurvivesTheCarryoverReview_AndApply()
    {
        using var h = new WallTestHarness();
        var (w, session, oldId, detectionId) = await AnsweredAsync(h);
        var cards = CarryoverScope.CardVerdictOldHoldIds(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));

        // What CarryoverReview seeds: carry-all, the matcher's twins, then the restored verdicts through the scope reconcile.
        var restored = await w.Sessions.GetDecisionsAsync(w.WallId);
        var seeded = (session.CarriedOldHoldIds ?? []).ToDictionary(id => id, id => new CarryoverDecision(id, CarryKind.Carried, null));
        foreach (var (old, twin) in CarryoverScope.DefaultTwins(session).Where(t => seeded.ContainsKey(t.Key)))
        {
            seeded[old] = new CarryoverDecision(old, CarryKind.Carried, twin);
        }

        var reconciled = CarryoverScope.Reconcile(session, restored.Carryover, cards);
        Assert.Empty(reconciled.Reset);
        foreach (var d in reconciled.Decisions.Where(d => seeded.ContainsKey(d.OldHoldId)))
        {
            seeded[d.OldHoldId] = d;
        }

        await w.Sessions.SaveCarryOutcomeAsync(w.WallId, [.. seeded.Values], restored.AcceptedNewCenterHoldIds, restored.RemovedNewCenterHoldIds, w.SessionId);

        var actors = new WallRefreshActors(null!, null!, w.Service, w.Sessions, null);
        var promotable = await RefreshDecisions.LoadAsync(w.WallId, actors, session, w.StagedByPanel);
        Assert.Equal(0, promotable.Resets);
        await w.Service.PromoteAsync(w.WallId, promotable.Scoped with { ExpectedMovesVersion = promotable.Moves.Version }, w.SessionId);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == oldId);
        Assert.NotEqual(detectionId, link.NewHoldId);
        Assert.Equal(2, await db.Holds.CountAsync(x => x.Generation == 3 && Math.Abs(x.X - SideX) < 0.05 && Math.Abs(x.Y - SideY) < 0.05));
    }

    // Kept only while it still says what the card answered: a row rewritten since is judged like any other.
    [Fact]
    public async Task ACardVerdict_RewrittenSince_IsResetLikeAnyOther()
    {
        using var h = new WallTestHarness();
        var (w, session, oldId, _) = await AnsweredAsync(h);
        var cards = CarryoverScope.CardVerdictOldHoldIds(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));

        var removed = new CarryoverDecision(oldId, CarryKind.Removed, null);
        Assert.Equal([removed], CarryoverScope.Reconcile(session, [removed], cards).Reset);
        var answered = new CarryoverDecision(oldId, CarryKind.Carried, null);
        Assert.Empty(CarryoverScope.Reconcile(session, [answered], cards).Reset);
    }

    // Every scope reconcile of the step-by-step wizard passes the card verdicts: restore, confirm counts, seed and Apply.
    [Fact]
    public void EveryWizardReconcile_PassesTheCardVerdicts()
    {
        var shared = Path.Combine(RepoRoot(), "src/Blocwerk.Web/Components/Shared");
        var calls = Directory.GetFiles(shared, "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"CarryoverScope\.Reconcile\(([^()]*)\)").Select(m => (File: Path.GetFileName(f), Args: m.Groups[1].Value)))
            .ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.True(c.Args.Split(',').Length == 3, $"{c.File}: {c.Args}"));
        var wizard = string.Concat(Directory.GetFiles(shared, "BigWallUpdate.*.cs").Select(File.ReadAllText));
        Assert.Equal(2, Regex.Matches(wizard, @"await LoadCardVerdictsAsync\(\);\s+var \w+ = ReconcileCarryScope\(").Count);
    }

    private static async Task<(LookWall Wall, BigUpdateSession Session, Guid OldId, Guid DetectionId)> AnsweredAsync(WallTestHarness h)
    {
        var w = await SeedAsync(h, targetDetected: true, neighbour: new LookNeighbour(SideX, SideY, OldHold: true));
        w.Probe.Set(SideX, SideY, 0.4);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        var oldId = w.NeighbourTargetId!.Value;
        var card = (await w.Sessions.GetUpdateExceptionsAsync(w.WallId))
            .Single(c => c.Kind == UpdateExceptionKind.LowConfidenceMatch && c.OldHoldId == oldId);
        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card.Id, UpdateExceptionAnswer.NotSame, w.SessionId);
        return (w, session, oldId, w.NeighbourDetectionId!.Value);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
