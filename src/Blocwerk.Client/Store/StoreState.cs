// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>What the offline client may show from the device cache (client-store.js's snapshot states).</summary>
public enum StoreState
{
    /// <summary>The store has not read the device yet: nothing is shown.</summary>
    Loading,

    /// <summary>The active partition holds at least one wall the page may read.</summary>
    Ready,

    /// <summary>Nothing is saved for the active partition (or no partition is active).</summary>
    Empty,

    /// <summary>A signed-in partition is locked after its session expired: only the sign-in prompt and Clear show.</summary>
    Locked,

    /// <summary>The ledger's upgrade waits for another tab: "Updating offline data…".</summary>
    Updating,

    /// <summary>The device cache cannot be read (storage refused, or the shared modules missing).</summary>
    Unavailable,

    /// <summary>
    /// A kiosk tablet with nothing it may show (PR 3.15): its bound wall's kiosk-class data is not on the device (never
    /// cached, or a <c>no-store</c> tablet). A tablet that holds it is <see cref="Ready"/>, with that one wall.
    /// </summary>
    Kiosk,
}
