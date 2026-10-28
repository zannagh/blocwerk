// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>The <c>wall</c> document: what a wall view draws before any hold or boulder.</summary>
/// <param name="V">The schema version (<see cref="DataDocumentSchema.Version"/>).</param>
/// <param name="WallId">The wall.</param>
/// <param name="Name">The wall's name.</param>
/// <param name="Description">The wall's description, if any.</param>
/// <param name="Angle">The wall's overall angle in degrees.</param>
/// <param name="Generation">The wall's live generation.</param>
/// <param name="PhotoSha">The single-image wall photo's sha (the fallback of the origin panel), or null.</param>
/// <param name="Border">The wall's border outline, or null.</param>
/// <param name="Segments">The segments, by sort order then id.</param>
/// <param name="Panels">The live panels (the newest with a photo per cell), by row then column.</param>
public sealed record WallDocument(
    int V,
    Guid WallId,
    string Name,
    string? Description,
    int Angle,
    int Generation,
    string? PhotoSha,
    IReadOnlyList<DocumentPoint>? Border,
    IReadOnlyList<WallDocumentSegment> Segments,
    IReadOnlyList<WallDocumentPanel> Panels);
