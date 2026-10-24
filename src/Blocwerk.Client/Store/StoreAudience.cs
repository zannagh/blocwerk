// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>
/// The document class the device's cached data of one wall was built for, as far as the client can tell. Only the
/// class's own documents are ever on the device; this decides the client-side rules on top of them (the outsider purge).
/// </summary>
public enum StoreAudience
{
    /// <summary>
    /// A signed-in device whose row says the wall was in the viewer's own list when cached: the members' or setters'
    /// class (drafts possible).
    /// </summary>
    Member,

    /// <summary>
    /// A share link's class: an anonymous device, or a signed-in one that cached a wall it is no member of, whatever URL
    /// the client is on now. Never sees drafts.
    /// </summary>
    Outsider,

    /// <summary>A kiosk tablet: the kiosk class, built like the outsider one.</summary>
    Kiosk,
}
