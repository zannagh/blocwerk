// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Entities;

/// <summary>
/// How sharp a photo-real view (Gaussian splat) the splat worker trains: its quality profile
/// (<c>docker/splat-worker/splatworker/profiles.py</c>). Higher profiles train on larger images for
/// more steps and grow more splats; the worker steps down a profile when its memory cannot fit one.
/// Stored as its integer value; never renumber.
/// </summary>
public enum SplatQuality
{
    /// <summary>The pre-profile splat: 5000 steps at 1800 px. Minutes on a laptop.</summary>
    Draft = 0,

    /// <summary>The default: 15000 steps with the photos at 2400 px. About an hour on an Apple M4.</summary>
    High = 1,

    /// <summary>30000 steps at the photos' native size. Meant for a dedicated GPU.</summary>
    Max = 2,

    /// <summary>
    /// 50000 steps on the photos at up to 4096 px, up to 6 M splats. gsplat on a CUDA GPU with at least 12 GB only:
    /// offered when a 3D runner (or the splat worker) reports it can train it; a worker without it trains Max instead.
    /// </summary>
    Ultra = 3,
}
