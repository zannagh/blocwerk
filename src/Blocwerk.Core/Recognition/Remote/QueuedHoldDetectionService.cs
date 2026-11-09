// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// Hold detection as one interactive <c>detect-holds@2</c> job per photo: the runner returns the boxes, the app turns them
/// into holds itself (<see cref="RunnerHoldsResult.ToHolds"/>, the in-process detector's conversion). Runs in the app
/// (<paramref name="inProcess"/>, under the recognition gate) when detection is switched off, a runner could not replay the
/// app's detection, no wall is in scope, or <see cref="RecognitionCaller"/> falls back.
/// </summary>
public sealed class QueuedHoldDetectionService(
    IHoldDetectionService inProcess, RecognitionCaller caller, RunnerHoldDetection models, HoldDetectionSettings settings)
    : IHoldDetectionService
{
    private const string PhotoFile = "p01.jpg";

    /// <inheritdoc/>
    public async Task<List<DetectedHold>> DetectHoldsAsync(byte[] imageData, HoldDetectionParameters? parameters = null) =>
        await DetectRemoteAsync(imageData, CancellationToken.None) ?? await inProcess.DetectHoldsAsync(imageData, parameters);

    /// <summary>
    /// The runner's holds, or null when the app detects them itself (<see cref="DetectInAppAsync"/>): the caller decides
    /// where that runs, e.g. inside a gate slot it already holds. <paramref name="ct"/> cancels the job.
    /// </summary>
    public async Task<List<DetectedHold>?> DetectRemoteAsync(byte[] imageData, CancellationToken ct)
    {
        if (!settings.Enabled || RecognitionDetectRequest.For(settings, models) is not { } request)
        {
            return null;
        }

        var parts = new[] { RecognitionParts.Request(request), RecognitionParts.Image("photos", PhotoFile, imageData) };
        var memory = RecognitionParts.MemoryMb([imageData], together: false);
        return await caller.RunAsync(RecognitionKinds.DetectHolds, parts, memory, Read, ct);
    }

    /// <summary>The app's own detection (gated by the in-process service).</summary>
    public Task<List<DetectedHold>> DetectInAppAsync(byte[] imageData) => inProcess.DetectHoldsAsync(imageData);

    private static List<DetectedHold> Read(JsonElement result) =>
        RunnerHoldsResult.ParseBoxes(result).TryGetValue(PhotoFile, out var photo)
            ? RunnerHoldsResult.ToHolds(photo.Width, photo.Height, photo.Boxes)
            : throw new InvalidDataException("The result has no boxes for the photo.");
}
