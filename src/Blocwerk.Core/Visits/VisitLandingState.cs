// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Visits;

/// <summary>What the landing page of a visitor link shows, or where it sends the browser.</summary>
public enum VisitLandingState
{
    /// <summary>No such link.</summary>
    Invalid,

    /// <summary>The link has expired, used or not.</summary>
    Expired,

    /// <summary>Another browser used the link.</summary>
    Used,

    /// <summary>Unused: offer to start the visit.</summary>
    Open,

    /// <summary>This browser used the link and the visit is on, or a member opened it: go to the wall.</summary>
    GoToWall,

    /// <summary>Somebody signed in who is not a member: visitor links are for guests, so it stays unused.</summary>
    SignedIn,
}
