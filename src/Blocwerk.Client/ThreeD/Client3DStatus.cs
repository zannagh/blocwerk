// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.ThreeD;

/// <summary>A wall's 3D on this device, as client-3d.js read it from the ledger and the caches (never the network).</summary>
/// <param name="State">"none", "off", "missing" or "ready" (<see cref="Client3DState"/>).</param>
/// <param name="Sha">The cached view3d document a ready state mounts, else null.</param>
/// <param name="PhotoReal">True when one of that model's splat levels is cached too, so photo-real is offered.</param>
public sealed record Client3DStatus(string? State, string? Sha, bool PhotoReal)
{
    /// <summary>Gets the status of a wall without 3D (and of every failure to read it).</summary>
    public static Client3DStatus None { get; } = new("none", null, false);

    /// <summary>Gets the state.</summary>
    public Client3DState Kind => State switch
    {
        "off" => Client3DState.Off,
        "missing" => Client3DState.Missing,
        "ready" when !string.IsNullOrEmpty(Sha) => Client3DState.Ready,
        _ => Client3DState.None,
    };
}
