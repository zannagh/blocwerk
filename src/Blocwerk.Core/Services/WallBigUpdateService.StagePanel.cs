// <copyright file="WallBigUpdateService.StagePanel.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition.Remote;

namespace Blocwerk.Core.Services;

/// <summary>The recognition half of a wall update: whether it may run at all, and staging one panel under one gate slot.</summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Refuses to stage or match an update while recognition is switched off: every staged hold would come out
    /// undetected and every old hold "not re-found", flooding the review. Discarding or promoting stays possible.
    /// </summary>
    private void EnsureRecognitionOn()
    {
        if (recognitionGate is { Enabled: false })
        {
            throw new ImageRecognitionUnavailableException(ImageRecognitionUnavailableException.DisabledMessage);
        }
    }

    /// <summary>
    /// Through the recognition queue every panel's detection is queued at once, so a second runner shortens the wall; the
    /// caller applies the results in panel order. Null in the app: one panel after the other, each in its own gate slot.
    /// </summary>
    private List<Task<List<DetectedHold>>>? QueueDetections(IReadOnlyList<BigUpdatePhoto> photos) =>
        holdDetectionService is QueuedHoldDetectionService
            ? photos.Select(p => holdDetectionService.DetectHoldsAsync(p.Image)).ToList()
            : null;

    /// <summary>
    /// Panel <paramref name="index"/>'s queued detection. When it throws, the later panels' detections are still observed
    /// (each of their jobs then ends on its own), so none of their failures goes unobserved.
    /// </summary>
    private static async Task<List<DetectedHold>> AwaitOrObserveRestAsync(List<Task<List<DetectedHold>>> queued, int index)
    {
        try
        {
            return await queued[index];
        }
        catch
        {
            foreach (var rest in queued.Skip(index + 1))
            {
                _ = rest.ContinueWith(t => t.Exception, TaskScheduler.Default);
            }

            throw;
        }
    }

    /// <summary>
    /// Detects one staged panel's holds (unless <paramref name="detected"/> already holds them), adds them at
    /// <paramref name="stagedGen"/> and enriches them.
    /// </summary>
    private async Task<List<Hold>> DetectAndEnrichPanelAsync(
        BlocwerkDbContext db, Wall wall, WallPanel panel, byte[] image, int stagedGen, List<DetectedHold>? detected = null)
    {
        detected ??= await holdDetectionService.DetectHoldsAsync(image);
        var staged = new List<Hold>(detected.Count);
        foreach (var d in detected)
        {
            staged.Add(new Hold
            {
                WallId = wall.Id,
                WallPanelId = panel.Id,
                X = d.X,
                Y = d.Y,
                Radius = d.Radius,
                DetectedBox = HoldDetectedBox.Of(d),
                Color = d.Color,
                Confidence = d.Confidence,
                IsAutoDetected = true,
                NeedsReview = true,
                Generation = stagedGen,
            });
        }

        db.Holds.AddRange(staged);
        await holdEnrichment.EnrichSafelyAsync(
            db, new HoldEnrichmentRequest(image, wall, staged, panel.Id, stagedGen, FromStagedPhoto: true), logger);
        return staged;
    }
}
