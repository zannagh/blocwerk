// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.ThreeD;

/// <summary>
/// The boulder a 3D view highlights, as wall3d.js's overlay carries it (the Server's <c>Wall3DOverlay</c>): its id and
/// name, the role of each of its holds by hold id, and the wall's holds of its "feet of one colour" rule.
/// </summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="Roles">Hold id (lowercase) → "Start", "Top", "Hand" or "Foot".</param>
/// <param name="ColorFoot">The other holds of its foot colour (lowercase ids): footholds by its rule.</param>
public sealed record Client3DBoulder(string Id, string Name, IReadOnlyDictionary<string, string> Roles, IReadOnlyList<string> ColorFoot);
