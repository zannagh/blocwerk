// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>
/// What a 3D runner found for the hold search (<see cref="RunnerHoldDetection"/>): the holds per camera name, and who found them,
/// <c>yolo-runner:&lt;model hash&gt;</c>, the key the detection cache keeps them under (never the app detector's own name).
/// </summary>
/// <param name="Detector">The runner detector's identity (the model it ran).</param>
/// <param name="Photos">The holds per camera name.</param>
public sealed record RunnerHoldsFound(string Detector, IReadOnlyDictionary<string, RunnerPhotoHolds> Photos);
