// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>ArUco markers as an interactive <c>markers</c> job; in the app when <see cref="RecognitionCaller"/> says so.</summary>
public sealed class QueuedMarkerDetectionService(IMarkerDetectionService inProcess, RecognitionCaller caller) : IMarkerDetectionService
{
    /// <inheritdoc/>
    public async Task<MarkerDetectionResult> DetectAsync(byte[] image, MarkerDetectionOptions? options, CancellationToken ct)
    {
        var o = options ?? MarkerDetectionOptions.Default;
        var request = new
        {
            version = 1,
            options = new { allowedIds = o.AllowedIds.Order().ToList(), o.MinSidePx, o.MaxEdgeRatio, o.RefineCorners, o.MinQuietZoneContrast, o.BrightenedPass },
        };
        var parts = new[] { RecognitionParts.Request(request), RecognitionParts.Image("photo", "photo.jpg", image) };
        return await caller.RunAsync(RecognitionKinds.Markers, parts, RecognitionParts.MemoryMb([image], together: false), RecognitionResults.Markers, ct)
            ?? await inProcess.DetectAsync(image, options, ct);
    }
}
