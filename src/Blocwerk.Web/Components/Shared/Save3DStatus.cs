// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary><c>bwAsset3d.status</c>: whether "Save 3D offline" is offered here and whether the wall's 3D is saved.</summary>
/// <param name="Available">The device cache runs for a signed-in viewer and is not streaming.</param>
/// <param name="Saved">The wall is kept offline with its 3D on the device.</param>
/// <param name="Cached">The wall's 3D is on the device (saved or not).</param>
public sealed record Save3DStatus(bool Available, bool Saved, bool Cached);
