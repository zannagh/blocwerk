// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>
/// The kiosk's offline rule on the C# side (Phase 3, PR 3.15), behind client-store.js's own: a tablet's <c>k-</c> label
/// is bound to one key and one wall, so a ready kiosk snapshot names exactly that one wall. Anything else (no wall, or a
/// second one beside it) is not the bound wall's alone, and the store shows <see cref="StoreState.Kiosk"/> ("Not
/// available offline") with no wall at all. Marks never reach a kiosk wall either way (<see cref="ClientStore"/> reads
/// them for <c>u-</c> labels only), whoever the tablet acts as.
/// </summary>
public static class KioskStoreGate
{
    /// <summary>True for a kiosk tablet's label.</summary>
    /// <param name="partition">A partition label, or null.</param>
    /// <returns>True when it is a <c>k-</c> label.</returns>
    public static bool IsKiosk(string? partition) => partition is not null && partition.StartsWith("k-", StringComparison.Ordinal);

    /// <summary>The state a snapshot lands as: a kiosk's ready snapshot only with exactly one wall.</summary>
    /// <param name="state">The state client-store.js reported.</param>
    /// <param name="snapshot">The snapshot it came with.</param>
    /// <returns><paramref name="state"/>, or <see cref="StoreState.Kiosk"/> for a kiosk snapshot that is not one wall's.</returns>
    public static StoreState Check(StoreState state, StoreSnapshot snapshot) =>
        state == StoreState.Ready && IsKiosk(snapshot.Partition) && snapshot.Walls.Count != 1 ? StoreState.Kiosk : state;
}
