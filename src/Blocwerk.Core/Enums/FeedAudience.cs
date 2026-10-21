// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Enums;

/// <summary>Who may receive a <see cref="Entities.WallChange"/>. Stored as a <c>smallint</c>; the values are persisted, never renumber them.</summary>
public enum FeedAudience : short
{
    /// <summary>Anyone who may view the wall, share-link and kiosk viewers included.</summary>
    Wall = 0,

    /// <summary>The wall's members only.</summary>
    Members = 1,

    /// <summary>The wall's setters and admins only (draft boulders).</summary>
    Setters = 2,

    /// <summary>One user only (<see cref="Entities.WallChange.AudienceUserId"/>), on any of their devices.</summary>
    User = 3,
}
