// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>Ends a <see cref="FeedScope"/>: closes it for everything that inherited it and restores the outer scope.</summary>
internal sealed class FeedScopeHandle : IDisposable
{
    private readonly FeedScopeState scope;

    public FeedScopeHandle(FeedScopeState scope)
    {
        this.scope = scope;
    }

    public void Dispose() => FeedScope.End(scope);
}
