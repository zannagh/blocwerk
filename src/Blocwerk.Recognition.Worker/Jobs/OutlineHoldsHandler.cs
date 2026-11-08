// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Recognition.Worker.Envelope;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary><c>outline-holds</c>: one <see cref="SessionHoldOutlineBatch"/> call on part <c>photo</c>, outlines in seed order.</summary>
public sealed class OutlineHoldsHandler(IHoldOutlineService outlines) : IRecognitionJobHandler
{
    public string Kind => "outline-holds";

    public async Task<object> RunAsync(RecognitionEnvelope envelope, CancellationToken ct)
    {
        var request = envelope.Request<OutlineRequest>();
        var photo = envelope.Image("photo");
        ct.ThrowIfCancellationRequested();
        try
        {
            var seeds = request.Seeds.Select(s => s.ToSeed()).ToList();
            var batch = await new SessionHoldOutlineBatch(outlines, gate: null)
                .OutlineAsync(photo, seeds, request.Options ?? HoldOutlineBatchOptions.Separately, ct);
            return ToResult(request.Seeds.Select(s => s.Id).ToList(), batch.Outlines);
        }
        catch (ArgumentException ex)
        {
            throw new RecognitionInputException($"The photo cannot be outlined: {ex.Message}");
        }
    }

    /// <summary>Contracts <c>jobs/recognition/outline-result</c>: one entry per seed id, polygon points as <c>[x, y]</c>.</summary>
    public static object ToResult(IReadOnlyList<int> ids, IReadOnlyList<HoldOutlineResult> outlines) => new
    {
        version = 1,
        outlines = outlines.Select((o, i) => new
        {
            id = ids[i],
            polygon = o.Polygon.Select(p => new[] { p.X, p.Y }),
            anchorX = o.AnchorX,
            anchorY = o.AnchorY,
            shapePoints = o.ShapePoints,
            shapeHoles = o.ShapeHoles,
            areaPx = o.AreaPx,
            bounds = o.Bounds,
            confidence = o.Confidence,
            method = o.Method,
            fingerprint = o.Fingerprint,
            corroborated = o.Corroborated,
            distinct = o.Distinct,
            pale = o.Pale,
        }),
    };
}
