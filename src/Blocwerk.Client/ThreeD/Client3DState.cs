// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.ThreeD;

/// <summary>What the device holds of a wall's 3D model (client-3d.js's <c>status</c>).</summary>
public enum Client3DState
{
    /// <summary>The device knows no 3D model of the wall: no 3D control shows.</summary>
    None,

    /// <summary>The viewer turned "Keep 3D on this device" off and the wall is not saved for offline 3D.</summary>
    Off,

    /// <summary>A model is known, but its document, textures or masks are not all cached.</summary>
    Missing,

    /// <summary>A document is cached with everything it draws.</summary>
    Ready,
}
