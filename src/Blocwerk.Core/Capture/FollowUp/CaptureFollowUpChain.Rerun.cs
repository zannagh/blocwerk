// <copyright file="CaptureFollowUpChain.Rerun.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>Running one recorded step again because its input changed (new wall textures re-place the holds).</summary>
public sealed partial class CaptureFollowUpChain
{
    /// <summary>
    /// Runs the step <paramref name="stepKey"/> again for the capture, recorded or not, without touching the capture's
    /// stage (its photo-real stage may be running). No-op when the capture's model is not the active one.
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="stepKey">The step's <see cref="ICaptureFollowUpStep.Key"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The step's new entry, or null when it did not run.</returns>
    public async Task<CaptureFollowUpEntry?> RerunAsync(Guid captureId, string stepKey, CancellationToken ct)
    {
        var (context, _, _) = await LoadAsync(captureId, ct);
        if (context is null)
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var step = scope.ServiceProvider.GetServices<ICaptureFollowUpStep>().FirstOrDefault(s => s.Key == stepKey);
        if (step is null)
        {
            return null;
        }

        var entry = await RunStepAsync(step, context, ct, quiet: true);
        await SaveAsync(captureId, c => c.FollowUpJson = CaptureFollowUpRecord.Parse(c.FollowUpJson).With(entry).ToJson(), ct);
        return entry;
    }

    /// <summary>
    /// For a record marked <see cref="CaptureFollowUpRecord.Rederive"/> (the capture's model was solved again and its
    /// record started over): runs every step (of every phase, in order) it does not have yet, without touching the stage,
    /// then clears the mark. No-op for an unmarked record or when the capture's model is not active.
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation (the mark stays, so a restart resumes the rest).</param>
    /// <returns>The record after this run.</returns>
    public async Task<CaptureFollowUpRecord> RunMissingAsync(Guid captureId, CancellationToken ct)
    {
        var (context, record, _) = await LoadAsync(captureId, ct);
        if (context is null || !record.Rederive)
        {
            return record;
        }

        await using var scope = scopes.CreateAsyncScope();
        foreach (var step in scope.ServiceProvider.GetServices<ICaptureFollowUpStep>().OrderBy(s => s.Order).Where(s => record.Find(s.Key) is null))
        {
            var inputsKey = step.RunsAfterCompletion ? await step.InputsKeyAsync(context, ct) : null;
            var entry = await RunStepAsync(step, context, ct, quiet: true) with { InputsKey = inputsKey };
            record = record.With(entry);
            await SaveAsync(captureId, c => c.FollowUpJson = CaptureFollowUpRecord.Parse(c.FollowUpJson).With(entry).ToJson(), ct);
        }

        await SaveAsync(captureId, c => c.FollowUpJson = (CaptureFollowUpRecord.Parse(c.FollowUpJson) with { Rederive = false }).ToJson(), ct);
        return record with { Rederive = false };
    }
}
