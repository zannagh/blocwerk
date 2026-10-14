// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Entities;

/// <summary>What a <see cref="GpuJob"/> asks a 3D runner to do. Stored as its integer value; never renumber.</summary>
public enum GpuJobKind
{
    /// <summary>Train the capture's photo-real view (a Gaussian splat).</summary>
    Splat = 0,

    /// <summary>
    /// Render the capture's wall textures (the wall-geometry renderer) on a machine with more memory than the server has.
    /// Only runners that advertise the <c>textures</c> capability get it.
    /// </summary>
    Textures = 1,

    /// <summary>
    /// Find the holds in a capture's photos for the hold proposal search (the app's model and tiling, run on the runner).
    /// Only runners that advertise the <c>detect-holds</c> capability get it; without one the app detects them itself.
    /// </summary>
    DetectHolds = 2,
}
