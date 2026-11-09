// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Configuration;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The <c>request</c> of a <c>detect-holds@2</c> job (contracts <c>jobs/recognition/detect-request</c>): the app's tiling
/// settings, its whole-image confidence and window IoU, and its model by hash, so a runner finds exactly the boxes the
/// app's detector would.
/// </summary>
public static class RecognitionDetectRequest
{
    /// <summary>The app detector's confidence with tiling off.</summary>
    public const double WholeImageConfidence = 0.25;

    /// <summary>The app detector's per-window NMS IoU.</summary>
    public const double WindowIoU = 0.45;

    /// <summary>The request, or null when a runner cannot replay the app's detection (no model file, settings it cannot replay).</summary>
    public static object? For(HoldDetectionSettings settings, RunnerHoldDetection models)
    {
        var tiling = settings.Tiling;
        if (!RunnerHoldsBundle.Replayable(tiling) || (tiling.Enabled && tiling.Overlap >= tiling.TileSize / 2)
            || models.ModelFile() is not { } file || models.ModelSha256() is not { } sha)
        {
            return null;
        }

        return new
        {
            version = 1,
            tiling,
            wholeImageConfidence = WholeImageConfidence,
            windowIoU = WindowIoU,
            model = new { name = Path.GetFileName(file), sha256 = sha },
        };
    }
}
