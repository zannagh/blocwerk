// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// One open <see cref="FeedAudienceScope"/>. Shared by reference with every flow that inherited it, so work that outlives
/// the operation sees <see cref="Closed"/> and stops narrowing.
/// </summary>
internal sealed class FeedAudienceScopeState
{
    private volatile bool closed;

    public FeedAudienceScopeState(FeedAudience audience, FeedAudienceScopeState? parent)
    {
        Audience = audience;
        Parent = parent;
    }

    /// <summary>Gets the narrowest audience in force, this scope's or a narrower outer one's.</summary>
    public FeedAudience Audience { get; }

    public FeedAudienceScopeState? Parent { get; }

    public bool Closed => closed;

    public void Close() => closed = true;
}
