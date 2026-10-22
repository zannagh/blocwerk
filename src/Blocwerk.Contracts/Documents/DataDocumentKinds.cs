// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>
/// The working-data documents of a wall beside <c>view3d</c> (design §6.5, Phase 3 plan §1), served on
/// <c>/api/walls/{w}/d/{kind}/{sha}</c> and listed by <c>/api/walls/{w}/docs</c>. Each is wall-level: nothing in one
/// depends on who reads it beyond the reader's audience class.
/// </summary>
public static class DataDocumentKinds
{
    /// <summary>The wall's metadata, segments and live panels (<see cref="WallDocument"/>).</summary>
    public const string Wall = "wall";

    /// <summary>The wall's live holds (<see cref="HoldsDocument"/>).</summary>
    public const string Holds = "holds";

    /// <summary>The wall's active boulders (<see cref="BouldersDocument"/>); drafts only in the setters class.</summary>
    public const string Boulders = "boulders";

    /// <summary>The wall's gallery images (<see cref="GalleryDocument"/>).</summary>
    public const string Gallery = "gallery";

    /// <summary>Gets every data document kind, in the order the index lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Wall, Holds, Boulders, Gallery];

    /// <summary>True for a data document kind (view3d is not one: it has its own store).</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}
