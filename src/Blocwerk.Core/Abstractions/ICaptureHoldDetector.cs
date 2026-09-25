// <copyright file="ICaptureHoldDetector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Proposals;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Finds holds in one capture photo for the multi-view hold proposals. The baseline is the app's own tiled YOLO
/// on the CPU (<see cref="Services.InAppCaptureHoldDetector"/>). A GPU path (a bigger detector or a segmentation
/// model) plugs in as another implementation, e.g. a <c>detect-holds</c> job kind on the compute protocol served
/// by a GPU runner (<see cref="Services.ComputeCaptureHoldDetector"/>, a stub today). The proposal run takes the
/// first AVAILABLE detector in registration order, so without a GPU runner it silently uses the CPU one.
/// </summary>
public interface ICaptureHoldDetector
{
    /// <summary>A short name for logs ("yolo-cpu", "gpu-runner").</summary>
    string Name { get; }

    /// <summary>Whether this detector can run now (a GPU runner online, a model file present).</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when usable.</returns>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>The holds in one photo, in its pixels (volumes excluded).</summary>
    /// <param name="photo">The photo's camera name.</param>
    /// <param name="image">The encoded photo.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The detections.</returns>
    Task<IReadOnlyList<CaptureDetection>> DetectAsync(string photo, byte[] image, CancellationToken ct = default);
}
