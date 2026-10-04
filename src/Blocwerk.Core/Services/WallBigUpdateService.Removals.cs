// <copyright file="WallBigUpdateService.Removals.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// The confirm screen's 3D evidence: old holds not found again whose spot shows bare wall on the new photo and on this
/// visit's 3D texture ("possibly removed", <see cref="RemovalCheck"/>), and detections the photo check discards while the
/// 3D model sees a hold there. Evidence only, never a decision; without a presence probe, an old photo, a model built
/// after the photos were staged or a registration at the spot there is simply nothing to report.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>How long a run's removal checks may probe in all before the rest stay unknown (default 2 min).</summary>
    internal TimeSpan RemovalCheckBudget { get; set; } = DefaultRemovalCheckBudget;

    private static TimeSpan DefaultRemovalCheckBudget =>
        int.TryParse(Environment.GetEnvironmentVariable("BLOCWERK_REMOVAL_CHECK_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(2);

    /// <summary>The old holds at a grid position that were not found again and have a predicted spot.</summary>
    private static List<Hold> RemovableAt(RemovalInputs removals, int col, int row) =>
        (removals.OldByPosition.GetValueOrDefault((col, row)) ?? [])
            .Where(h => removals.NotFoundAgain.Contains(h.Id) && removals.Warp.ContainsKey(h.Id))
            .ToList();

    /// <summary>Discarded detections the 3D model of this visit sees as a hold: the photo and the model disagree.</summary>
    private static void CollectConflicts(
        NewHoldTriageOutcome result,
        Guid panelId,
        Guid modelId,
        PanelEvidence3D evidence,
        IReadOnlyList<Hold> unpaired,
        PanelTriage? outcome,
        IReadOnlyDictionary<Guid, Evidence3DVerdict>? verdicts)
    {
        if (outcome is null || verdicts is null || !evidence.FromThisVisit)
        {
            return;
        }

        foreach (var hold in unpaired)
        {
            if (verdicts.GetValueOrDefault(hold.Id) != Evidence3DVerdict.SeenIn3D || !outcome.Discards.TryGetValue(hold.Id, out var reason))
            {
                continue;
            }

            var spot = TextureSpotLocator.Locate(evidence.Evidence.Registrations, hold.X, hold.Y);
            result.ConflictingNew.Add(new ConflictingNewHold(hold.Id, panelId, reason, modelId, spot?.FacetId, spot?.A, spot?.B));
        }
    }

    /// <summary>Runs <see cref="RemovalCheck"/> on one staged panel and records its "bare wall" verdicts.</summary>
    private async Task CheckRemovalsAsync(
        BlocwerkDbContext db,
        Evidence3DModel model,
        PanelEvidence3D evidence,
        RemovalScope scope,
        IReadOnlyDictionary<Guid, HoldPositionNorm> warp,
        NewHoldTriageOutcome result)
    {
        if (scope.Candidates.Count == 0 || !evidence.FromThisVisit || presenceProbe is not { } probe || scope.OldPhoto is not { } oldPhoto)
        {
            return;
        }

        // One budget for the whole run, shared by its panels: what is left of it when this panel's check starts.
        result.RemovalDeadline ??= DateTimeOffset.UtcNow + RemovalCheckBudget;
        var left = result.RemovalDeadline.Value - DateTimeOffset.UtcNow;
        using var budget = new CancellationTokenSource(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        try
        {
            var newPhoto = await db.WallPanels.Where(p => p.Id == scope.PanelId).Select(p => p.StagedPhoto).FirstAsync();
            model.Textures ??= (await ActiveModelTextures.LoadAsync(db, captureFiles!, model.Id, model.Json, logger, CancellationToken.None)).Textures;
            if (newPhoto is null || model.Textures.Count == 0
                || OverlapSeedLoader.RawSize(newPhoto) is not { } newSize || OverlapSeedLoader.RawSize(oldPhoto) is not { } oldSize)
            {
                return;
            }

            var panel = RemovalPanelOf(scope, warp, evidence, model.Textures, newSize, oldSize);
            var images = model.Textures.GroupBy(t => t.Frame.FacetId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Image, StringComparer.Ordinal);
            var findings = await Task.Run(() => RemovalCheck.Run(
                panel,
                q => probe.Score(oldPhoto, newPhoto, q),
                (facet, q) => images.TryGetValue(facet, out var texture) ? probe.Score(texture, oldPhoto, q) : q.Select(_ => (double?)null).ToList(),
                budget.Token));
            if (budget.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Removal check on panel {PanelId} ran out of time ({Budget}); the holds not probed yet stay unknown", scope.PanelId, RemovalCheckBudget);
            }

            var bare = findings.Where(f => f is { Verdict: RemovalVerdict.BareWall, Spot: not null, PhotoScore: not null, TextureScore: not null }).ToList();
            result.PossiblyRemoved.AddRange(bare.Select(f => new PossiblyRemovedHold(
                f.OldHoldId, scope.PanelId, f.X, f.Y, model.Id, f.Spot!.FacetId, f.Spot.A, f.Spot.B, f.PhotoScore!.Value, f.TextureScore!.Value)));
            logger.LogInformation(
                "Removal check on panel {PanelId}: {Bare} of {Candidates} old holds not found again show bare wall ({Present} show a hold)",
                scope.PanelId, bare.Count, findings.Count, findings.Count(f => f.Verdict == RemovalVerdict.HoldPresent));
        }
        catch (Exception ex)
        {
            // Contained whatever it is (a probe's cancellation or timeout too): the shutdown token never reaches this
            // service, and the refresh goes on without evidence rather than failing for it.
            logger.LogWarning(ex, "Removal check failed on panel {PanelId}; no hold is reported as possibly removed", scope.PanelId);
        }
    }

    private static RemovalPanel RemovalPanelOf(
        RemovalScope scope,
        IReadOnlyDictionary<Guid, HoldPositionNorm> warp,
        PanelEvidence3D evidence,
        IReadOnlyList<RegistrationTexture> textures,
        (int Width, int Height) newSize,
        (int Width, int Height) oldSize)
    {
        var longSide = Math.Max(newSize.Width, newSize.Height);
        var candidates = scope.Candidates
            .Select(h => new RemovalCandidate(
                h.Id, h.X * oldSize.Width, h.Y * oldSize.Height, warp[h.Id].X * newSize.Width, warp[h.Id].Y * newSize.Height, h.Radius * longSide))
            .ToList();
        var pairs = scope.Twins
            .Select(t => new PointPair(t.New.X * newSize.Width, t.New.Y * newSize.Height, t.Old.X * oldSize.Width, t.Old.Y * oldSize.Height))
            .ToList();
        var staged = scope.Staged.Select(h => (h.X * newSize.Width, h.Y * newSize.Height)).ToList();
        var frames = textures.GroupBy(t => t.Frame.FacetId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Frame, StringComparer.Ordinal);
        return new RemovalPanel(candidates, pairs, staged, newSize, evidence.Evidence, frames, evidence.FromThisVisit);
    }
}
