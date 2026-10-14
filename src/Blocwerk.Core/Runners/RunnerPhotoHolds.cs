// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.Proposals;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The holds a 3D runner found in one capture photo, as the app's own detector reports them (<see cref="RunnerHoldsResult.ToDetections"/>):
/// volumes left out, in the photo's raw pixels. <paramref name="Width"/> and <paramref name="Height"/> are the decoded size the
/// runner saw, so the hold search checks the orientation and scales onto the solved camera exactly as for a photo it decodes itself.
/// </summary>
/// <param name="Width">The photo's decoded width.</param>
/// <param name="Height">The photo's decoded height.</param>
/// <param name="Detections">The holds found.</param>
public sealed record RunnerPhotoHolds(int Width, int Height, IReadOnlyList<CaptureDetection> Detections);
