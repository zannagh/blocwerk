// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary><c>bwAsset3d.save</c>: whether the wall's 3D ended up saved (false when the browser refused the storage).</summary>
/// <param name="Saved">The wall is kept offline with its 3D on the device.</param>
public sealed record Save3DResult(bool Saved);
