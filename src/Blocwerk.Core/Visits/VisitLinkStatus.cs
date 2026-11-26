// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Visits;

/// <summary>What a visitor link is, as its landing page has to tell it.</summary>
public enum VisitLinkStatus
{
    /// <summary>No such link: never made, mistyped, or cleaned up long after it expired.</summary>
    NotFound,

    /// <summary>Unused and unexpired: the first browser to start the visit gets it.</summary>
    Open,

    /// <summary>Used already, and not expired. Only the browser that used it may go on.</summary>
    Redeemed,

    /// <summary>Past its expiry, used or not. Expired wins over used.</summary>
    Expired,
}
