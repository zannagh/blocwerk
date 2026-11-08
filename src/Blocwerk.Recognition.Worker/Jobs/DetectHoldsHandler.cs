// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.HoldDetection.Tiling;
using Blocwerk.Recognition.Worker.Envelope;
using Blocwerk.Recognition.Worker.Models;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>
/// <c>detect-holds@2</c>: the model by hash (<see cref="ModelStore"/>), then the app's tiled YOLO pass per <c>photos</c> part.
/// The result is <c>jobs/detect-holds/holds-result</c>, keyed by the photo part's file name.
/// </summary>
public sealed class DetectHoldsHandler(ModelStore models, YoloBoxDetector detector) : IRecognitionJobHandler
{
    public string Kind => "detect-holds@2";

    public async Task<object> RunAsync(RecognitionEnvelope envelope, CancellationToken ct)
    {
        var request = envelope.Request<DetectRequest>();
        if (Math.Abs(request.WindowIoU - YoloTiledDetector.WindowIoU) > 1e-9 || (request.Tiling.Enabled && request.Tiling.Overlap >= request.Tiling.TileSize / 2))
        {
            throw new RecognitionInputException("The request asks for tiling this detector cannot replay.");
        }

        var photos = envelope.Images("photos");
        detector.Load(await models.ResolveAsync(request.Model.Sha256, ct), request.Model.Sha256);
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (file, bytes) in photos)
        {
            ct.ThrowIfCancellationRequested();
            var (width, height, boxes) = detector.Detect(bytes, request, ct);
            result[file] = new { width, height, boxes };
        }

        return new { version = 1, photos = result };
    }
}
