// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.Corrections;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>What a carry did.</summary>
/// <param name="Placed">Live holds placed on the target model afterwards.</param>
/// <param name="Unmeasured">Holds that lost their placement (they sat on a dropped facet).</param>
/// <param name="Restored">Holds restored exactly by reverting an earlier carry (a re-activated version).</param>
/// <param name="VolumesCopied">Volumes copied (mapped) onto the target model.</param>
public sealed record CorrectionCarryResult(int Placed, int Unmeasured, int Restored, int VolumesCopied);

/// <summary>
/// Carries the data derived on one model version onto another that a chain of corrections connects it to: a correction
/// is a known similarity of its parent (<see cref="CorrectionEdge"/>), so its follow-ups do not register the photos again
/// (which is slow and, for a weak photo, gives a different answer); the parent's placements, footprints, protrusions,
/// volumes and hold proposals are mapped instead (<see cref="WallGeometryModelTransformer.TransformHold"/> and its
/// siblings), holds on a dropped facet become unmeasured.
/// </summary>
/// <remarks>
/// <para>Each step is recorded as a <see cref="Entities.HoldPlacementRun"/> on the target model (trigger
/// <see cref="Services.HoldPlacementTrigger.Correction"/>) with every hold's previous values, so it can be reverted like any
/// run, and the target's automatic placement sees the holds as placed on it.</para>
/// <para>Going back up (re-activating the version a correction was derived from) first reverts the carry that brought
/// the data down, exactly, wherever nobody changed the hold since; only the rest is mapped by the inverse similarity.
/// So re-activating a parent restores its placements and keeps its own runs revertable.</para>
/// </remarks>
public static partial class CorrectionCarry
{
    /// <summary>
    /// Carries the wall's derived data from <paramref name="fromModelId"/> (whose frame it is in: the model that was
    /// active) to <paramref name="toModelId"/>. Saves, in one transaction. Null (nothing changed) when no chain of
    /// corrections connects the two or a correction's similarity cannot be read.
    /// </summary>
    /// <param name="db">An admin context of the wall.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="fromModelId">The model the data is in.</param>
    /// <param name="toModelId">The model it is carried to.</param>
    /// <param name="userId">Who is acting (stored on the runs).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was done, or null.</returns>
    public static async Task<CorrectionCarryResult?> CarryAsync(
        BlocwerkDbContext db, Guid wallId, Guid fromModelId, Guid toModelId, Guid userId, CancellationToken ct = default)
    {
        var steps = await StepsAsync(db, wallId, fromModelId, toModelId, ct);
        if (steps is not { Count: > 0 })
        {
            return null;
        }

        var owns = db.Database.CurrentTransaction is null;
        await using var transaction = owns ? await db.Database.BeginTransactionAsync(ct) : null;
        CorrectionCarryResult? last = null;
        var (restored, unmeasured, copied) = (0, 0, 0);
        foreach (var step in steps)
        {
            last = await StepAsync(db, wallId, step, userId, ct);
            (restored, unmeasured, copied) = (restored + last.Restored, unmeasured + last.Unmeasured, copied + last.VolumesCopied);
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return new CorrectionCarryResult(last!.Placed, unmeasured, restored, copied);
    }

    /// <summary>The steps with their similarity and dropped facet, or null when a step's correction cannot be read.</summary>
    private static async Task<List<CarryStep>?> StepsAsync(BlocwerkDbContext db, Guid wallId, Guid from, Guid to, CancellationToken ct)
    {
        var models = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId)
            .Select(m => new { m.Id, m.DerivedFromModelId, m.Source })
            .ToListAsync(ct);
        var nodes = models.ToDictionary(
            m => m.Id, m => new LineageNode(m.Id, m.DerivedFromModelId, m.Source.StartsWith(WallGeometryCorrectionService.SourcePrefix, StringComparison.Ordinal)));
        if (ModelLineage.Path(nodes, from, to) is not { Count: > 0 } path)
        {
            return null;
        }

        var ids = path.SelectMany(s => new[] { s.From, s.To }).Distinct().ToList();
        var json = await db.WallGeometryModels.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.Json, ct);
        var steps = new List<CarryStep>();
        foreach (var step in path)
        {
            if (CorrectionEdge.Of(json[step.Child], json[step.Parent]) is not { } edge)
            {
                return null;
            }

            steps.Add(step.Up
                ? new CarryStep(step.From, step.To, edge.Transform.Inverse(), null)
                : new CarryStep(step.From, step.To, edge.Transform, edge.DroppedFacet));
        }

        return steps;
    }
}

/// <summary>One carry step: data in <see cref="From"/>'s frame mapped into <see cref="To"/>'s.</summary>
/// <param name="From">The model the data is in.</param>
/// <param name="To">The model it is carried to.</param>
/// <param name="Transform">From's world → To's world.</param>
/// <param name="DroppedFacet">A facet To does not have (its holds become unmeasured), or null.</param>
internal sealed record CarryStep(Guid From, Guid To, GeometrySimilarity Transform, string? DroppedFacet);
