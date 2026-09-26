// <copyright file="WallCaptureGeometryMode.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Entities;

/// <summary>How a <see cref="WallCapture"/> measures the wall. Stored as its integer value; never renumber.</summary>
public enum WallCaptureGeometryMode
{
    /// <summary>The marker solve: at least two photos show usable printed markers (and every capture before the choice existed).</summary>
    Markers = 0,

    /// <summary>
    /// No (usable) markers: a feature reconstruction (splat-worker <c>splat-prepare</c>) and wall-geometry's <c>solve-sfm</c>.
    /// Scale and gravity may be estimates (<see cref="Geometry.WallGeometryWorld.ScaleSource"/>).
    /// </summary>
    Features = 1,
}
