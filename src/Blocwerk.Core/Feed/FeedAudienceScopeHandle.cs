// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>Ends a <see cref="FeedAudienceScope"/>: restores the narrowing that was in force before it.</summary>
internal sealed class FeedAudienceScopeHandle : IDisposable
{
    private readonly FeedAudience? outer;

    public FeedAudienceScopeHandle(FeedAudience? outer)
    {
        this.outer = outer;
    }

    public void Dispose() => FeedAudienceScope.Reset(outer);
}
