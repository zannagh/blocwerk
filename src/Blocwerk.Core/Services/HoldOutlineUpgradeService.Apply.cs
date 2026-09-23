using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Writing an upgrade: batch by batch, each batch saved together with the run record.</summary>
public sealed partial class HoldOutlineUpgradeService
{
    /// <inheritdoc/>
    public async Task<HoldOutlineUpgradeResult> ApplyAsync(
        Guid wallId, HoldOutlineUpgradeOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        EnsureEnabled();
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            // The run row exists before the first hold is written, and every batch updates its entry list in
            // the SAME SaveChanges as the holds — so whatever was written is always revertable, even if the
            // run is cut short.
            var run = new HoldOutlineUpgradeRun { WallId = wallId, CreatedByUserId = userId, IncludedManual = options.IncludeManual };
            db.HoldOutlineUpgradeRuns.Add(run);
            await db.SaveChangesAsync(ct);

            var model = await LoadMetricModelAsync(db, wallId, ct);
            var entries = new List<HoldOutlineUpgradeEntry>();
            var skipped = 0;
            foreach (var photo in await LoadLivePhotosAsync(db, wallId, ct))
            {
                var plan = await PlanPhotoAsync(db, wallId, photo, options.IncludeManual, model, ct);
                if (plan is null)
                {
                    continue;
                }

                run.EligibleCount += plan.Proposals.Count;
                run.KeptCircleCount += plan.Proposals.Count(p => p.Outcome != HoldOutlineUpgradeOutcome.Outline);
                var writes = plan.Proposals.Where(p => p.Outcome == HoldOutlineUpgradeOutcome.Outline || p.FillsFingerprint);
                foreach (var batch in writes.Chunk(BatchSize))
                {
                    skipped += await WriteBatchAsync(db, run, batch, plan.Metrics, entries, ct);
                }
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Outline upgrade {RunId} on wall {WallId} by {UserId}: {Eligible} circle holds, {Outlined} outlined, "
                + "{Holes} with holes, {Fingerprinted} fingerprinted, {Measured} measured, {Skipped} skipped (changed meanwhile)",
                run.Id, wallId, userId, run.EligibleCount, run.OutlinedCount, run.WithHolesCount,
                run.FingerprintedCount, run.MeasuredCount, skipped);
            return new HoldOutlineUpgradeResult(
                run.Id, run.EligibleCount, run.OutlinedCount, run.KeptCircleCount, run.WithHolesCount,
                run.FingerprintedCount, run.MeasuredCount, skipped);
        }
    }

    /// <summary>
    /// Re-reads the batch's holds tracked, skips any that changed since they were planned, writes the rest and
    /// saves them together with the run's grown entry list. Returns the number skipped.
    /// </summary>
    private static async Task<int> WriteBatchAsync(
        BlocwerkDbContext db,
        HoldOutlineUpgradeRun run,
        HoldOutlineUpgradeProposal[] batch,
        Dictionary<Hold, HoldMetric> metrics,
        List<HoldOutlineUpgradeEntry> entries,
        CancellationToken ct)
    {
        var ids = batch.Select(p => p.Hold.Id).ToList();
        var holds = await db.Holds.Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var skipped = 0;
        foreach (var proposal in batch)
        {
            if (!holds.TryGetValue(proposal.Hold.Id, out var hold) || ChangedSincePlanned(hold, proposal.Hold))
            {
                skipped++;
                continue;
            }

            var entry = Write(hold, proposal, metrics.GetValueOrDefault(proposal.Hold));
            entries.Add(entry);
            Count(run, proposal, entry);
        }

        run.HoldIdsJson = HoldOutlineUpgradeEntry.ToJson(entries);
        await db.SaveChangesAsync(ct);
        return skipped;
    }

    /// <summary>An edit landed between planning and writing: the plan no longer describes this hold.</summary>
    private static bool ChangedSincePlanned(Hold current, Hold planned) =>
        current.ShapePoints is { Count: > 0 }
        || current.IsVirtual
        || current.X != planned.X
        || current.Y != planned.Y
        || current.Radius != planned.Radius
        || current.FingerprintJson != planned.FingerprintJson;

    /// <summary>
    /// Writes one proposal. X/Y/Radius are never touched and nothing here flags a boulder: a better
    /// drawing of the same hold is not a physical change.
    /// </summary>
    private static HoldOutlineUpgradeEntry Write(Hold hold, HoldOutlineUpgradeProposal proposal, HoldMetric? metric)
    {
        var outline = proposal.Outcome == HoldOutlineUpgradeOutcome.Outline;
        var entry = new HoldOutlineUpgradeEntry
        {
            HoldId = hold.Id,
            PrevShapeEmpty = hold.ShapePoints is { Count: 0 },
            PrevOutlineSource = hold.OutlineSource,
            PrevFingerprintJson = hold.FingerprintJson,
            PrevMetric = outline && metric is not null ? HoldMetricSnapshot.Of(hold) : null,
        };
        var fingerprintBefore = hold.FingerprintJson;

        if (outline)
        {
            hold.ShapePoints = proposal.Result.ShapePoints;
            hold.ShapeHoles = proposal.HasHoles ? proposal.Result.ShapeHoles : null;
            hold.OutlineSource = HoldOutlineSource.AutoContour;
            hold.OutlineConfidence = proposal.Result.Confidence;
        }

        if (proposal.FillsFingerprint)
        {
            hold.FingerprintJson = proposal.Result.Fingerprint.ToJson();
        }

        if (entry.PrevMetric is not null)
        {
            HoldMetricPlanner.Apply(hold, metric!);
        }

        return entry with
        {
            ShapeHash = outline ? HoldOutlineUpgradeEntry.HashShape(hold) : null,
            FingerprintHash = hold.FingerprintJson != fingerprintBefore ? HoldOutlineUpgradeEntry.HashFingerprint(hold.FingerprintJson) : null,
        };
    }

    private static void Count(HoldOutlineUpgradeRun run, HoldOutlineUpgradeProposal proposal, HoldOutlineUpgradeEntry entry)
    {
        if (entry.ShapeHash is not null)
        {
            run.OutlinedCount++;
            run.WithHolesCount += proposal.HasHoles ? 1 : 0;
        }

        run.FingerprintedCount += proposal.FillsFingerprint ? 1 : 0;
        run.MeasuredCount += entry.PrevMetric is not null ? 1 : 0;
    }
}
