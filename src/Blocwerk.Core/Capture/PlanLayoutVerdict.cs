// <copyright file="PlanLayoutVerdict.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>A detection the plan check ignored, with the admin-facing reason.</summary>
/// <param name="Marker">The detection.</param>
/// <param name="Detail">Why it does not fit ("mirrored against markers 13 and 15", …).</param>
public sealed record IgnoredCaptureMarker(CaptureMarker Marker, string Detail);

/// <summary>What the plan check kept and ignored in one photo.</summary>
/// <param name="Kept">Detections that fit the plan (or could not be judged).</param>
/// <param name="Ignored">Detections that do not fit the plan layout.</param>
public sealed record PlanLayoutVerdict(IReadOnlyList<CaptureMarker> Kept, IReadOnlyList<IgnoredCaptureMarker> Ignored);
