// <copyright file="LookOverDeck.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// Code-behind of the look-over's card deck: which card of the queue is shown (<see cref="DeckCursor"/>), and the
/// last answer. The page runs every answer and hands back the queue as it is then; the card that was answered has
/// left it by that, so the next one is shown without a tap. A card whose answer was just taken back is turned to again.
/// </summary>
public partial class LookOverDeck
{
    private const int WindowSize = 60;

    private readonly DeckCursor cursor = new();
    private LookOverDeckCard? card;
    private LookOverMissing? missingCard;
    private Guid? lastId;
    private int shownQueueKey;

    [Parameter]
    [EditorRequired]
    public Guid WallId { get; set; }

    /// <summary>The open findings, in the order they are asked about.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<LookOverFindingView> Queue { get; set; } = [];

    /// <summary>Every finding the page lists, answered ones included: the bar after an answer reads its result here.</summary>
    [Parameter]
    public IReadOnlyList<LookOverFindingView> All { get; set; } = [];

    /// <summary>An answer is on its way.</summary>
    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Changes whenever the page has loaded the list again.</summary>
    [Parameter]
    public int Version { get; set; }

    /// <summary>Changes when the page shows another queue (another filter): the deck starts at its first card again.</summary>
    [Parameter]
    public int QueueKey { get; set; }

    /// <summary>What the deck shows when its queue is empty.</summary>
    [Parameter]
    public RenderFragment? Empty { get; set; }

    [Parameter]
    public EventCallback<LookOverChoice> OnAnswer { get; set; }

    [Parameter]
    public EventCallback<LookOverAction> OnAct { get; set; }

    [Parameter]
    public EventCallback<Guid> OnUndo { get; set; }

    private int Index => cursor.Index;

    private LookOverFindingView? Current => Queue.Count == 0 ? null : Queue[Index];

    private LookOverFindingView? LastAnswered =>
        lastId is { } id ? All.FirstOrDefault(f => f.Id == id && f.Status != LookOverFindingStatus.Open) : null;

    /// <summary>The part of the queue the list beside the card shows: a stretch around the current card.</summary>
    private int WindowStart => Math.Clamp(Index - (WindowSize / 3), 0, Math.Max(0, Queue.Count - WindowSize));

    private List<LookOverFindingView> Window => Queue.Skip(WindowStart).Take(WindowSize).ToList();

    /// <summary>Turns to a finding of the queue (from the list view, say).</summary>
    /// <param name="findingId">The finding.</param>
    public void JumpTo(Guid findingId) => cursor.JumpTo(findingId);

    protected override void OnParametersSet()
    {
        if (shownQueueKey != QueueKey)
        {
            shownQueueKey = QueueKey;
            cursor.Reset();
        }

        cursor.Sync([.. Queue.Select(f => f.Id)]);
    }

    /// <summary>A number key gives the answer with that number; U takes the last answer back.</summary>
    private async Task OnKeyAsync(string key)
    {
        if (key == "u")
        {
            if (LastAnswered is { CanUndo: true } last && !Disabled)
            {
                await UndoLastAsync(last.Id);
            }
        }
        else if (int.TryParse(key, out var number))
        {
            var press = Current is { Missing: true } ? missingCard?.PressAsync(number) : card?.PressAsync(number);
            if (press is not null)
            {
                await press;
            }
        }
    }

    private void Step(int by) => cursor.Step(by);

    private Task AnswerAsync(LookOverChoice choice)
    {
        lastId = choice.FindingId;
        return OnAnswer.InvokeAsync(choice);
    }

    private Task ActAsync(LookOverAction action)
    {
        lastId = action.FindingId;
        return OnAct.InvokeAsync(action);
    }

    /// <summary>Takes the last answer back and turns to that card again: it returns to its place in the queue.</summary>
    private Task UndoLastAsync(Guid findingId)
    {
        cursor.ReturnTo(findingId);
        return OnUndo.InvokeAsync(findingId);
    }
}
