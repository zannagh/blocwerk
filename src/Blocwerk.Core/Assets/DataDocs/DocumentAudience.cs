// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// The audience classes the data documents are built, stored and served for (Phase 3 plan §1). A sha is served only
/// under the caller's own class, so a document never reaches a class it was not built for, even by a known sha.
/// </summary>
public enum DocumentAudience
{
    /// <summary>The wall's owner, admins and moderators (the feed's Setters audience): the only class with drafts.</summary>
    Setters,

    /// <summary>A plain member of the wall: no drafts.</summary>
    Members,

    /// <summary>A share-link viewer who is not a member, signed in or not: no drafts.</summary>
    Outsider,

    /// <summary>
    /// A kiosk tablet on its own wall, whoever it acts as (an acting setter included): built like
    /// <see cref="Outsider"/>, because one partition on the tablet serves every identity it can take.
    /// </summary>
    Kiosk,
}
