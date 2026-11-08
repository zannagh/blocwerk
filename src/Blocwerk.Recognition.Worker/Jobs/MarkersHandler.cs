// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Recognition.Worker.Envelope;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary><c>markers</c>: <see cref="IMarkerDetectionService.DetectAsync"/> on part <c>photo</c>.</summary>
public sealed class MarkersHandler(IMarkerDetectionService markers) : IRecognitionJobHandler
{
    public string Kind => "markers";

    public async Task<object> RunAsync(RecognitionEnvelope envelope, CancellationToken ct)
    {
        var options = envelope.Request<MarkersRequest>().ToOptions();
        return ToResult(await markers.DetectAsync(envelope.Image("photo"), options, ct));
    }

    /// <summary>Contracts <c>jobs/recognition/markers-result</c>: points as <c>[x, y]</c>, reasons by name.</summary>
    public static object ToResult(MarkerDetectionResult r) => new
    {
        version = 1,
        imageWidth = r.ImageWidth,
        imageHeight = r.ImageHeight,
        markers = r.Markers.Select(m => new
        {
            id = m.Id,
            cornersPx = Points(m.CornersPx),
            cornersNormalized = Points(m.CornersNormalized),
            sidePx = m.SidePx,
            edgeRatio = m.EdgeRatio,
            synthetic = m.Synthetic,
        }),
        rejected = r.Rejected.Select(c => new
        {
            id = c.Id,
            cornersPx = Points(c.CornersPx),
            sidePx = c.SidePx,
            edgeRatio = c.EdgeRatio,
            reason = c.Reason.ToString(),
            detail = c.Detail,
        }),
        undecodedCandidateCount = r.UndecodedCandidateCount,
        suspicious = r.Suspicious,
        warnings = r.Warnings,
    };

    private static double[][] Points(IEnumerable<MarkerPoint> points) => points.Select(p => new[] { p.X, p.Y }).ToArray();
}
