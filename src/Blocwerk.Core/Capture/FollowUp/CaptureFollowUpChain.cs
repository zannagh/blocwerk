// <copyright file="CaptureFollowUpChain.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// The post-capture chain: once a capture's model is live, every registered <see cref="ICaptureFollowUpStep"/>
/// runs in <see cref="ICaptureFollowUpStep.Order"/> — place the existing holds, refine their shapes, measure them
/// in the photo-real view, … — so new photos improve what the wall already has without a single extra click.
/// </summary>
/// <remarks>
/// <para>Idempotent and resumable: each step's outcome is written onto the capture as soon as it finishes, and a
/// recorded step never runs again (a photo-real step runs again only for a different photo-real view). A restart
/// in the middle of a step leaves nothing recorded for it, so the resumed capture runs exactly that step again.</para>
/// <para>Isolated: a step that throws is recorded as failed and the next step still runs. Only shutdown
/// (cancellation) stops the chain.</para>
/// </remarks>
public sealed class CaptureFollowUpChain(RootDbContextFactory dbContextFactory, IServiceScopeFactory scopes, ILogger<CaptureFollowUpChain> logger)
{
    /// <summary>Runs the steps due in <paramref name="phase"/> that have not run yet.</summary>
    /// <param name="captureId">The capture (it must have produced the wall's active model).</param>
    /// <param name="phase">Right after the model went live, or at the end of the capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The record after this run (unchanged when the chain could not run).</returns>
    public async Task<CaptureFollowUpRecord> RunAsync(Guid captureId, CaptureFollowUpPhase phase, CancellationToken ct)
    {
        var (context, record) = await LoadAsync(captureId, ct);
        if (context is null)
        {
            return record;
        }

        await using var scope = scopes.CreateAsyncScope();
        var steps = scope.ServiceProvider.GetServices<ICaptureFollowUpStep>().OrderBy(s => s.Order).ToList();
        foreach (var step in steps.Where(s => IsDue(s, phase, record, context)))
        {
            var entry = await RunStepAsync(step, context, ct);
            record = record.With(entry);
            await SaveAsync(captureId, c => c.FollowUpJson = record.ToJson(), ct);
        }

        return record;
    }

    /// <summary>Whether the step still has to run for this capture in this phase.</summary>
    internal static bool IsDue(ICaptureFollowUpStep step, CaptureFollowUpPhase phase, CaptureFollowUpRecord record, CaptureFollowUpContext context)
    {
        var recorded = record.Find(step.Key);
        if (!step.NeedsPhotoReal)
        {
            return recorded is null;
        }

        return phase == CaptureFollowUpPhase.Final && (recorded is null || recorded.SplatId != context.SplatId);
    }

    private async Task<CaptureFollowUpEntry> RunStepAsync(ICaptureFollowUpStep step, CaptureFollowUpContext context, CancellationToken ct)
    {
        await SaveAsync(context.CaptureId, c => c.Stage = step.Title.Length <= 200 ? step.Title : step.Title[..200], ct);
        CaptureFollowUpStepResult result;
        try
        {
            result = await step.RunAsync(context, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: follow-up step {Step} failed; the next steps still run", context.CaptureId, step.Key);
            result = new CaptureFollowUpStepResult(CaptureFollowUpOutcome.Failed, $"{step.Title} failed");
        }

        logger.LogInformation(
            "Capture {CaptureId} follow-up {Step}: {Outcome} — {Summary}",
            context.CaptureId, step.Key, result.Outcome, string.IsNullOrWhiteSpace(result.Summary) ? "nothing to report" : result.Summary);
        return new CaptureFollowUpEntry(
            step.Key, result.Outcome, result.Summary, DateTimeOffset.UtcNow, step.NeedsPhotoReal ? context.SplatId : null);
    }

    /// <summary>The capture's context (null when its model is not the wall's active one) and its record so far.</summary>
    private async Task<(CaptureFollowUpContext? Context, CaptureFollowUpRecord Record)> LoadAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = await db.WallCaptures.AsNoTracking()
            .Where(c => c.Id == captureId)
            .Select(c => new { c.WallId, c.GeometryModelId, c.CreatedByUserId, c.FollowUpJson })
            .FirstOrDefaultAsync(ct);
        if (capture?.GeometryModelId is not { } modelId)
        {
            return (null, CaptureFollowUpRecord.Empty);
        }

        var record = CaptureFollowUpRecord.Parse(capture.FollowUpJson);
        var active = await db.WallGeometryModels.AnyAsync(m => m.Id == modelId && m.WallId == capture.WallId && m.IsActive, ct);
        if (!active)
        {
            logger.LogInformation("Capture {CaptureId}: its model {ModelId} is not the active one; no follow-up steps", captureId, modelId);
            return (null, record);
        }

        var splatId = await db.WallGeometrySplats.AsNoTracking()
            .Where(s => s.GeometryModelId == modelId)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);
        return (new CaptureFollowUpContext(captureId, capture.WallId, modelId, capture.CreatedByUserId, splatId), record);
    }

    private async Task SaveAsync(Guid captureId, Action<Entities.WallCapture> change, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var capture = await db.WallCaptures.FirstAsync(c => c.Id == captureId, ct);
        change(capture);
        await db.SaveChangesAsync(ct);
    }
}
