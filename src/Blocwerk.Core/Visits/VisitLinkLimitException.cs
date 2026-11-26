// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Visits;

/// <summary>A member made <see cref="WallVisitService.HourlyLimit"/> visitor links within the last hour.</summary>
public sealed class VisitLinkLimitException : InvalidOperationException
{
    public VisitLinkLimitException()
        : base($"You made {WallVisitService.HourlyLimit} visitor links in the last hour. Try again a little later.")
    {
    }
}
