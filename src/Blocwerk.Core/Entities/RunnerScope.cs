// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Entities;

/// <summary>Who a <see cref="GpuRunner"/> belongs to (runner protocol 1.2, §1.5).</summary>
public enum RunnerScope
{
    /// <summary>A user's runner with a <c>bwr_</c> key, serving the walls its owner administers (and approved shared ones).</summary>
    Owned = 0,

    /// <summary>
    /// The deployment's recognition runner: no owner, key from <c>RECOGNITION__INSTANCEKEY</c>, every wall, recognition kinds
    /// only, never shown in or shared through the runners UI.
    /// </summary>
    Instance = 1,
}
