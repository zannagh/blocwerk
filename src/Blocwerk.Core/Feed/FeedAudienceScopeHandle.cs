// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>Ends a <see cref="FeedAudienceScope"/>: closes it for everything that inherited it and restores the outer one.</summary>
internal sealed class FeedAudienceScopeHandle : IDisposable
{
    private readonly FeedAudienceScopeState state;

    public FeedAudienceScopeHandle(FeedAudienceScopeState state)
    {
        this.state = state;
    }

    public void Dispose() => FeedAudienceScope.End(state);
}
