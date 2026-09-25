// <copyright file="WallCaptureStatusList.Coverage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Components.Shared;

/// <summary>Which captures show "Next capture: what to add" (<see cref="WallCaptureCoverage"/>).</summary>
public partial class WallCaptureStatusList
{
    /// <summary>The newest done capture: its list is shown open above the history.</summary>
    private WallCaptureSummary? LatestDone => running is null ? history.FirstOrDefault(IsDone) : null;

    /// <summary>A done capture with a model has a coverage report (or gets one on first read).</summary>
    private static bool IsDone(WallCaptureSummary capture) =>
        capture.GeometryModelId is not null
        && capture.Status is WallCaptureStatus.Succeeded or WallCaptureStatus.SucceededWithoutTextures or WallCaptureStatus.SucceededWithoutSplat;
}
