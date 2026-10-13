// <copyright file="DeckCursor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Which card of a card deck's queue is shown. The card being asked about keeps its place while it is in the queue;
/// once it has left (it was answered), the one that took its place is shown, so the next card comes without a tap.
/// A card whose answer was just taken back is turned to again. Pure.
/// </summary>
public sealed class DeckCursor
{
    private IReadOnlyList<Guid> queue = [];
    private Guid? currentId;
    private Guid? returnTo;
    private int index;

    /// <summary>Gets the place of the shown card in the queue (0 when the queue is empty).</summary>
    public int Index => Math.Clamp(index, 0, Math.Max(0, queue.Count - 1));

    /// <summary>Gets the shown card, or null when the queue is empty.</summary>
    public Guid? Current => queue.Count == 0 ? null : queue[Index];

    /// <summary>Takes the queue as it is now (after an answer, a reload, another filter).</summary>
    /// <param name="ids">The cards of the queue, in order.</param>
    public void Sync(IReadOnlyList<Guid> ids)
    {
        queue = ids;
        var back = returnTo is { } undone ? Find(undone) : -1;
        var at = back >= 0 ? back : currentId is { } id ? Find(id) : -1;
        index = at >= 0 ? at : Index;
        returnTo = back >= 0 ? null : returnTo;
        currentId = Current;
    }

    /// <summary>One card on (1) or back (-1), around the ends.</summary>
    /// <param name="by">How many cards to move.</param>
    public void Step(int by)
    {
        if (queue.Count == 0)
        {
            return;
        }

        index = (((Index + by) % queue.Count) + queue.Count) % queue.Count;
        currentId = Current;
    }

    /// <summary>Turns to a card of the queue; a card that is not in it changes nothing.</summary>
    /// <param name="id">The card.</param>
    public void JumpTo(Guid id)
    {
        var at = Find(id);
        if (at >= 0)
        {
            (index, currentId) = (at, id);
        }
    }

    /// <summary>The answer of this card is being taken back: when it is in the queue again, it is the shown card.</summary>
    /// <param name="id">The card.</param>
    public void ReturnTo(Guid id) => returnTo = id;

    /// <summary>Starts at the first card again (the queue is another one now).</summary>
    public void Reset() => (index, currentId, returnTo) = (0, null, null);

    private int Find(Guid id)
    {
        for (var i = 0; i < queue.Count; i++)
        {
            if (queue[i] == id)
            {
                return i;
            }
        }

        return -1;
    }
}
