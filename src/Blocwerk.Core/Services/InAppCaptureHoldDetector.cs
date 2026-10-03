// <copyright file="InAppCaptureHoldDetector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry.Proposals;
using SkiaSharp;

namespace Blocwerk.Core.Services;

/// <summary>
/// The CPU baseline of <see cref="ICaptureHoldDetector"/>: the app's hold detector (tiled YOLO on ONNX Runtime) on
/// the photo's raw pixel grid, the grid the cameras were solved on. Boxes of the "volume" class (reported white)
/// are left out.
/// </summary>
public sealed class InAppCaptureHoldDetector(IHoldDetectionService detector) : ICaptureHoldDetector
{
    private const string VolumeColor = "white";

    /// <inheritdoc />
    public string Name => "yolo-cpu";

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaptureDetection>> DetectAsync(string photo, byte[] image, CancellationToken ct = default)
    {
        using var codec = SKCodec.Create(new MemoryStream(image));
        if (codec is null)
        {
            return [];
        }

        var (w, h) = (codec.Info.Width, codec.Info.Height);
        var longSide = Math.Max(w, h);
        var holds = await detector.DetectHoldsAsync(image);
        return holds
            .Where(d => d.Color != VolumeColor)
            .Select(d => new CaptureDetection(photo, d.X * w, d.Y * h, d.Radius * longSide, d.Confidence))
            .ToList();
    }
}
