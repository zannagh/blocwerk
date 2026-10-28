// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Components;

namespace Blocwerk.Client.ThreeD;

/// <summary>The 3D view's side in the browser (client-3d.js, client-3d-stage.js); a fake in the tests.</summary>
public interface IClient3D
{
    /// <summary>What the device holds of the wall's 3D, read from the device alone.</summary>
    /// <param name="partition">The store's label.</param>
    /// <param name="wallId">The wall.</param>
    /// <returns>The status; <see cref="Client3DStatus.None"/> on any failure.</returns>
    Task<Client3DStatus> StatusAsync(string partition, Guid wallId);

    /// <summary>Mounts the viewer into <paramref name="element"/>.</summary>
    /// <param name="element">The stage element, which the viewer then owns.</param>
    /// <param name="request">What to mount.</param>
    /// <returns>"ok", or why not (client-3d-stage.js).</returns>
    Task<string> MountAsync(ElementReference element, Client3DRequest request);

    /// <summary>Frees the viewer of <paramref name="element"/> and its WebGL context.</summary>
    /// <param name="element">The stage element.</param>
    /// <returns>A task.</returns>
    Task UnmountAsync(ElementReference element);
}
