// <copyright file="CaptureGeometryOverride.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>How a wall admin wants a capture measured when starting it (for testing and comparing the two paths).</summary>
public enum CaptureGeometryOverride
{
    /// <summary>The pipeline decides: markers when at least two photos show them, else photo features when available.</summary>
    Auto = 0,

    /// <summary>The marker solve only: refused at the start when the photos show fewer than two markers.</summary>
    Markers = 1,

    /// <summary>Photo features even when markers are present: refused at the start when the services can't run it.</summary>
    Features = 2,
}
