using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection;

/// <summary>
/// Outcome of the mat/floor false-detection filter: the detections that survive
/// (<see cref="Kept"/>) and the ones classified as likely crash-mat / floor blobs
/// (<see cref="Dropped"/>). Kept + Dropped always partitions the input.
/// </summary>
public sealed record MatFilterResult(
    IReadOnlyList<DetectedHold> Kept,
    IReadOnlyList<DetectedHold> Dropped);
