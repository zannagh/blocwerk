// <copyright file="ComputeCaptureHoldDetector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry.Proposals;

namespace Blocwerk.Core.Services;

/// <summary>
/// STUB for the optional GPU path of <see cref="ICaptureHoldDetector"/>. Planned interface: a <c>detect-holds</c>
/// job kind on the Blocwerk compute job protocol (docker/compute-jobs-protocol.md), served by a GPU runner like the
/// splat training: request = the capture's photos (multipart, stripped) + options (model, tile size, min
/// confidence); result = <c>{ "photos": { "p01": [ { "x", "y", "w", "h", "confidence", "label", "mask"? } ] } }</c>
/// in each photo's raw pixels. A larger detector or a segmentation model there would also give outlines for the
/// best-view footprints. Until a runner advertises the kind this reports unavailable, so the proposal run uses the
/// CPU detector and nothing waits.
/// </summary>
public sealed class ComputeCaptureHoldDetector : ICaptureHoldDetector
{
    /// <inheritdoc />
    public string Name => "gpu-runner";

    /// <inheritdoc />
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);

    /// <inheritdoc />
    public Task<IReadOnlyList<CaptureDetection>> DetectAsync(string photo, byte[] image, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CaptureDetection>>([]);
}
