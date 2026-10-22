// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One hold of a <see cref="HoldsDocument"/>: what the overlay draws it from.</summary>
/// <param name="Id">The hold.</param>
/// <param name="PanelId">Its panel; null for a legacy centre-photo or virtual hold.</param>
/// <param name="Generation">Its generation.</param>
/// <param name="X">Its normalised centre X.</param>
/// <param name="Y">Its normalised centre Y.</param>
/// <param name="Radius">Its normalised radius.</param>
/// <param name="Color">Its colour, if set.</param>
/// <param name="Category">Its category, by name.</param>
/// <param name="HandType">Its hand type, by name, if set.</param>
/// <param name="OnKickboard">True when it sits on the kickboard.</param>
/// <param name="Virtual">True for a virtual hold (no photo of its own).</param>
/// <param name="Kept">
/// True for a hold kept unconfirmed from an earlier generation (<c>KeptUnconfirmedGeneration</c> set, the Server's
/// <c>PanelHold.Kept</c>): the wall view hides it, and a boulder that uses one draws it faded and dashed.
/// </param>
/// <param name="StackedOn">The hold it is stacked on, if any.</param>
/// <param name="Shape">The outer outline as offsets from the centre, if any.</param>
/// <param name="Control">The outline's spline control points, if any.</param>
/// <param name="Holes">The outline's hole rings, if any.</param>
public sealed record HoldsDocumentHold(
    Guid Id,
    Guid? PanelId,
    int Generation,
    double X,
    double Y,
    double Radius,
    string? Color,
    string Category,
    string? HandType,
    bool OnKickboard,
    bool Virtual,
    bool Kept,
    Guid? StackedOn,
    IReadOnlyList<DocumentPoint>? Shape,
    IReadOnlyList<DocumentPoint>? Control,
    IReadOnlyList<IReadOnlyList<DocumentPoint>>? Holes);
