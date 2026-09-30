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
}
