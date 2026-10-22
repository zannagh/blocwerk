// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One segment of a <see cref="WallDocument"/>.</summary>
/// <param name="Id">The segment.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">Its kind, by name (<c>Wall</c>, <c>Kickboard</c>, ...).</param>
/// <param name="Angle">Its angle in degrees.</param>
/// <param name="Yaw">Its yaw in degrees.</param>
/// <param name="SortOrder">Its place in the list.</param>
/// <param name="Points">Its outline.</param>
public sealed record WallDocumentSegment(
    Guid Id,
    string Name,
    string Kind,
    int Angle,
    int Yaw,
    int SortOrder,
    IReadOnlyList<DocumentPoint> Points);
