// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.ThreeD;

/// <summary>A mount of the 3D viewer (client-3d-stage.js's <c>mount</c>), serialised once for the call.</summary>
/// <param name="Partition">The store's label the status was read under.</param>
/// <param name="WallId">The wall.</param>
/// <param name="Sha">The ready view3d document.</param>
/// <param name="Token">The share link's token, or null.</param>
/// <param name="Boulder">The boulder to highlight, or null.</param>
/// <param name="Mode">The start mode: "schematic", "photos" or "photoreal" (held back for the client's first ~20 s).</param>
/// <param name="RoleColors">The colours of the hold roles, as on the 2D views.</param>
public sealed record Client3DRequest(
    string Partition,
    string WallId,
    string Sha,
    string? Token,
    Client3DBoulder? Boulder,
    string Mode,
    IReadOnlyDictionary<string, string> RoleColors);
