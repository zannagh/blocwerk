// <copyright file="ICaptureHoldDetector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Proposals;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Finds holds in one capture photo for the multi-view hold proposals. The baseline is the app's own tiled YOLO
/// on the CPU (<see cref="Services.InAppCaptureHoldDetector"/>). Runners detect through the capture follow-up instead
/// (<c>detect-holds@2</c> recognition jobs, then <c>detect-holds</c> 1.0 on GPU runners), before this is asked. Another
/// detector plugs in as another implementation: the proposal run takes the first AVAILABLE one in registration order.
/// </summary>
public interface ICaptureHoldDetector
{
    /// <summary>A short name for logs ("yolo-cpu").</summary>
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
