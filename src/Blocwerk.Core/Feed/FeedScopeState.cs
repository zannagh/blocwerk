// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// One open <see cref="FeedScope"/>. The ambient value is a reference to this object, so work that inherited it (a
/// <c>Task.Run</c>, a fire-and-forget continuation) sees <see cref="Closed"/> once the operation has ended and stops
/// collapsing.
/// </summary>
public sealed class FeedScopeState
{
    private volatile bool closed;

    internal FeedScopeState(Guid wallId, string label, FeedScopeState? parent)
    {
        WallId = wallId;
        Label = label;
        Parent = parent;
    }

    /// <summary>Gets the wall the operation is about; it takes the resync of a change no mapper could attribute.</summary>
    public Guid WallId { get; }

    /// <summary>Gets the operation's label, written as the resync reason (never user input).</summary>
    public string Label { get; }

    /// <summary>Gets the scope this one was opened inside, or null.</summary>
    public FeedScopeState? Parent { get; }

    /// <summary>Gets a value indicating whether the scope's operation has ended.</summary>
    public bool Closed => closed;

    internal void Close() => closed = true;
}
