// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The answer of <c>bwAsset3d.gate</c> (wwwroot/js/asset-3d.js): ask before starting the 3D view, and how much it downloads.</summary>
/// <param name="Ask">Show the mobile-data notice first.</param>
/// <param name="Bytes">What the wall's 3D still needs on this device, from its manifest; null when unknown.</param>
public sealed record Wall3DDataGate(bool Ask, long? Bytes);
