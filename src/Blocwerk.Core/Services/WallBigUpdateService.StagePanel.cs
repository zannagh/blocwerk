// <copyright file="WallBigUpdateService.StagePanel.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;
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
    /// caller applies the results in panel order. A queued task only waits for a runner: null means the app detects that
    /// panel itself, later, inside the panel's own gate slot. The first one that throws cancels the others' jobs through
    /// <paramref name="stop"/>. Null in the app: one panel after the other, each in its own gate slot.
    /// </summary>
    private List<Task<List<DetectedHold>?>>? QueueDetections(IReadOnlyList<BigUpdatePhoto> photos, CancellationTokenSource stop) =>
        holdDetectionService is QueuedHoldDetectionService queued
            ? photos.Select(p => FirstFailureStopsTheRestAsync(queued.DetectRemoteAsync(p.Image, stop.Token), stop)).ToList()
            : null;

    private static async Task<List<DetectedHold>?> FirstFailureStopsTheRestAsync(Task<List<DetectedHold>?> detection, CancellationTokenSource stop)
    {
        try
        {
            return await detection;
        }
        catch
        {
            await stop.CancelAsync();
            throw;
        }
    }

    /// <summary>
    /// Cancels the queued detections still running and waits until each has ended, so none is left unobserved; returns the
    /// first failure that was not a cancellation, or null.
    /// </summary>
    private static async Task<Exception?> StopQueuedAsync(List<Task<List<DetectedHold>?>> queued, CancellationTokenSource stop)
    {
        await stop.CancelAsync();
        try
        {
            await Task.WhenAll(queued);
        }
        catch (Exception)
        {
            // Read from the tasks below.
        }

        return queued.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException).FirstOrDefault(e => e is not OperationCanceledException);
    }

    /// <summary>
    /// Adds the staged panel of <paramref name="photo"/> and detects and enriches its holds (<paramref name="queued"/>: its
    /// detection on a recognition runner, if any); returns the panel's id when it is the centre (0,0).
    /// </summary>
    private async Task<Guid?> StageOnePanelAsync(
        BlocwerkDbContext db, Wall wall, BigUpdatePhoto photo, int stagedGen, Guid userId, Task<List<DetectedHold>?>? queued)
    {
        // Stored at full resolution (already stripped of location/metadata by StageAsync, pixels
        // and EXIF orientation untouched): hold detection and the panel matcher below must see the
        // camera's original. The browser is served downscaled variants instead, generated on
        // demand from these originals (see IImageVariantCache).
        var image = photo.Image;
        var panel = new WallPanel
        {
            WallId = wall.Id,
            Col = photo.Col,
            Row = photo.Row,
            Photo = null,
            StagedPhoto = image,
            StagedPhotoContentType = photo.ContentType,
            StagedAt = DateTimeOffset.UtcNow,
            StagedByUserId = userId,
            Generation = stagedGen,
        };
        db.WallPanels.Add(panel);

        // One gate slot for the panel's detection AND enrichment: the next panel (or another user's
        // run) only starts decoding once this one's rasters and model buffers are gone. A runner's
        // detection needs no slot (the enrichment then takes its own); the app's, if it falls back, runs inside it.
        var detected = queued is null ? null : await queued;
        var staged = recognitionGate is null || detected is not null
            ? await DetectAndEnrichPanelAsync(db, wall, panel, image, stagedGen, detected)
            : await recognitionGate.RunAsync("stage-panel", () => DetectAndEnrichPanelAsync(db, wall, panel, image, stagedGen, detected));
        await HoldStacker.StackNewAsync(db, staged);
        return photo.Col == 0 && photo.Row == 0 ? panel.Id : null;
    }

    /// <summary>
    /// Detects one staged panel's holds (unless <paramref name="detected"/> already holds them), adds them at
    /// <paramref name="stagedGen"/> and enriches them.
    /// </summary>
    private async Task<List<Hold>> DetectAndEnrichPanelAsync(
        BlocwerkDbContext db, Wall wall, WallPanel panel, byte[] image, int stagedGen, List<DetectedHold>? detected = null)
    {
        detected ??= holdDetectionService is QueuedHoldDetectionService queued
            ? await queued.DetectInAppAsync(image)
            : await holdDetectionService.DetectHoldsAsync(image);
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
