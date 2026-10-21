// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>On what grounds the caller may read a wall's hashed assets (see <see cref="AssetAccessGate"/>).</summary>
public enum AssetAccess
{
    /// <summary>Not at all: the route answers 404.</summary>
    None,

    /// <summary>A signed-in member of the wall (an act-as kiosk session included, pinned to its wall).</summary>
    Member,

    /// <summary>A caller holding the wall's current share token who is not a member.</summary>
    ShareToken,

    /// <summary>A registered kiosk tablet with nobody picked, on the one wall it is bound to.</summary>
    KioskAnonymous,
}
