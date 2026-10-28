// <copyright file="PanelHoldDocuments.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The Server's panel holds as the shared overlay (PanelHoldOverlay) takes them: the holds document's hold. Only what
/// the overlay reads is carried; the generation, hand type, kickboard flag and control points it never draws stay at
/// their defaults.
/// </summary>
public static class PanelHoldDocuments
{
    /// <summary>The holds of panel <paramref name="panelId"/> as document holds, in the same order.</summary>
    /// <param name="holds">The panel's holds.</param>
    /// <param name="panelId">The panel they are drawn on.</param>
    /// <returns>The document holds.</returns>
    public static List<HoldsDocumentHold> ToDocument(IEnumerable<PanelHold> holds, Guid panelId) =>
        [.. holds.Select(h => new HoldsDocumentHold(
            h.Id, panelId, 0, h.X, h.Y, h.Radius, h.Color, h.Category.ToString(), null, false, h.IsVirtual, h.Kept, h.StackedOn,
            h.ShapePoints.AsDocumentRing(), null, h.ShapeHoles.AsDocumentRings()))];
}
