// <copyright file="LookOverDeck.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// Code-behind of the look-over's card deck: which card of the queue is shown, and the last answer. The page runs
/// every answer and hands back the queue as it is then; the card that was answered has left it by that, so the next
/// one is shown without a tap. A card whose answer was just taken back is turned to again.
/// </summary>
public partial class LookOverDeck : IAsyncDisposable
{
    private const int WindowSize = 60;
    private static readonly string[] Keys = ["ArrowLeft", "ArrowRight", "1", "2", "3", "4", "u"];

    private ElementReference root;
    private LookOverDeckCard? card;
    private LookOverMissing? missingCard;
    private KeyboardShortcutScope? keys;
    private IJSObjectReference? swipe;
    private DotNetObjectReference<LookOverDeck>? self;
    private Guid? currentId;
    private Guid? lastId;
    private Guid? returnTo;
    private int index;
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

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private int Index => Math.Clamp(index, 0, Math.Max(0, Queue.Count - 1));

    private LookOverFindingView? Current => Queue.Count == 0 ? null : Queue[Index];

    private LookOverFindingView? LastAnswered =>
        lastId is { } id ? All.FirstOrDefault(f => f.Id == id && f.Status != LookOverFindingStatus.Open) : null;

    /// <summary>The part of the queue the list beside the card shows: a stretch around the current card.</summary>
    private int WindowStart => Math.Clamp(Index - (WindowSize / 3), 0, Math.Max(0, Queue.Count - WindowSize));

    private List<LookOverFindingView> Window => Queue.Skip(WindowStart).Take(WindowSize).ToList();

    /// <summary>Turns to a finding of the queue (from the list view, say).</summary>
    /// <param name="findingId">The finding.</param>
    public void JumpTo(Guid findingId)
    {
        var at = Queue.ToList().FindIndex(f => f.Id == findingId);
        if (at >= 0)
        {
            (index, currentId) = (at, findingId);
        }
    }

    /// <summary>A swipe across the card: to the left is the next card, to the right the previous one.</summary>
    /// <param name="by">1 or -1.</param>
    [JSInvokable]
    public void OnSwipe(int by)
    {
        Step(by);
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (keys is not null)
        {
            await keys.DisposeAsync();
        }

        try
        {
            if (swipe is not null)
            {
                await swipe.InvokeVoidAsync("detach", root);
                await swipe.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // The page is gone; so is its listener.
        }

        self?.Dispose();
    }

    protected override void OnParametersSet()
    {
        // The card being asked about keeps its place while it is in the queue; once it has left, the one that took
        // its place is next. A card whose answer was just taken back is turned to again.
        var queue = Queue.ToList();
        if (shownQueueKey != QueueKey)
        {
            (shownQueueKey, currentId, index) = (QueueKey, null, 0);
        }

        var back = returnTo is { } undone ? queue.FindIndex(f => f.Id == undone) : -1;
        var at = back >= 0 ? back : currentId is { } id ? queue.FindIndex(f => f.Id == id) : -1;
        index = at >= 0 ? at : Index;
        returnTo = back >= 0 ? null : returnTo;
        currentId = Current?.Id;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        keys = new KeyboardShortcutScope(JS, OnShortcutAsync);
        await keys.RegisterAsync(Keys);
        try
        {
            self = DotNetObjectReference.Create(this);
            swipe = await JS.InvokeAsync<IJSObjectReference>("import", "/js/deck-swipe.js");
            await swipe.InvokeVoidAsync("attach", root, self);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // Without the swipe the arrows still turn the cards.
        }
    }

    private async Task OnShortcutAsync(string key)
    {
        if (key is "ArrowLeft" or "ArrowRight")
        {
            Step(key == "ArrowLeft" ? -1 : 1);
        }
        else if (key == "u")
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

        StateHasChanged();
    }

    private void Step(int by)
    {
        if (Queue.Count == 0)
        {
            return;
        }

        index = (Index + by + Queue.Count) % Queue.Count;
        currentId = Current?.Id;
    }

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
        returnTo = findingId;
        return OnUndo.InvokeAsync(findingId);
    }
}
