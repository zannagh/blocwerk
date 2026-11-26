// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Visits;

/// <summary>The landing page's one decision, apart from the page so it can be tested on its own.</summary>
public static class VisitLanding
{
    /// <summary>
    /// What to show for <paramref name="link"/>. Expired wins over every other answer, used included. A member who
    /// opens a link to their own wall goes there without using it; anybody else signed in is told the link is for
    /// guests. The browser that used the link goes back to the wall while the visit lasts.
    /// </summary>
    /// <param name="link">The link, as looked up.</param>
    /// <param name="signedIn">Whether somebody is signed in.</param>
    /// <param name="isMember">Whether the signed-in person already sees the wall.</param>
    /// <param name="grant">This browser's visit cookie, if it carries one.</param>
    public static VisitLandingState Decide(VisitLinkLookup link, bool signedIn, bool isMember, VisitGrant? grant)
    {
        if (link.Status == VisitLinkStatus.NotFound)
        {
            return VisitLandingState.Invalid;
        }

        if (link.Status == VisitLinkStatus.Expired)
        {
            return VisitLandingState.Expired;
        }

        if (signedIn)
        {
            return isMember ? VisitLandingState.GoToWall : VisitLandingState.SignedIn;
        }

        if (link.Status == VisitLinkStatus.Open)
        {
            return VisitLandingState.Open;
        }

        return link.IsRedeemedBy(grant) ? VisitLandingState.GoToWall : VisitLandingState.Used;
    }
}
