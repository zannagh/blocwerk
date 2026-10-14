// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>Which workers a start or redo of a capture sends its images to (<see cref="CaptureLimits.Refusal"/>).</summary>
[Flags]
public enum CaptureFit
{
    /// <summary>Nothing is sent.</summary>
    None = 0,

    /// <summary>The 3D model service (the photos: solve, textures).</summary>
    Geometry = 1,

    /// <summary>The photo-real worker (photos, video frames and anchor photos).</summary>
    Splat = 2,

    /// <summary>A capture's start: both.</summary>
    Start = Geometry | Splat,

    /// <summary>A retrain of the photo-real view.</summary>
    Retrain = Splat,

    /// <summary>A re-solve of the 3D model.</summary>
    Resolve = Geometry,

    /// <summary>A texture re-render on the server.</summary>
    Rerender = Geometry,
}
