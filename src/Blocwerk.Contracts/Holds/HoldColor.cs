// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Holds;

/// <summary>
/// One entry of the hold color palette.
/// </summary>
/// <param name="Key">The value persisted in <c>Hold.Color</c>.</param>
/// <param name="DisplayName">Label shown in pickers.</param>
/// <param name="Hex">Solid color used for swatches, strokes and schematic fills.</param>
/// <param name="StrokeHex">Darker companion used to outline the hold.</param>
public sealed record HoldColor(string Key, string DisplayName, string Hex, string StrokeHex);
