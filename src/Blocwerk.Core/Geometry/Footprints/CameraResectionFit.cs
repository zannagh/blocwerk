// <copyright file="CameraResectionFit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>A <see cref="CameraResection"/> result.</summary>
/// <param name="Centre">The camera centre, world mm.</param>
/// <param name="Kept">Pairs left after the outlier trimming.</param>
/// <param name="MedianError">Their median reprojection error, in the image points' units.</param>
public sealed record CameraResectionFit(double[] Centre, int Kept, double MedianError);
