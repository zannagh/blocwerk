// <copyright file="CardDeck.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind of the card deck's frame: the swipe and the keys. Which card is shown is the caller's to keep;
/// the deck only says "one back" or "one on" (<see cref="OnStep"/>) and hands on the keys it does not use itself.
/// </summary>
public partial class CardDeck : IAsyncDisposable
{
    private static readonly string[] Keys = ["ArrowLeft", "ArrowRight", "1", "2", "3", "4", "u"];

    private ElementReference root;
    private KeyboardShortcutScope? keys;
    private IJSObjectReference? swipe;
    private DotNetObjectReference<CardDeck>? self;

    /// <summary>How many cards the queue holds.</summary>
    [Parameter]
    [EditorRequired]
    public int Count { get; set; }

    /// <summary>Which of them is shown (0 is the first).</summary>
    [Parameter]
    public int Index { get; set; }

    /// <summary>What the shown card is about, beside the progress.</summary>
    [Parameter]
    public string? Label { get; set; }

    /// <summary>The card: a <c>.deck-body</c> and a <c>.deck-answers</c>.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>What is shown when the queue is empty.</summary>
    [Parameter]
    public RenderFragment? Empty { get; set; }

    /// <summary>A note above the answers (the last answer, with Undo); null for none.</summary>
    [Parameter]
    public RenderFragment? Note { get; set; }

    /// <summary>
    /// Whether "Skip" is offered beside the note. Off where a card without an answer and a card answered "keep" come to
    /// the same thing: the arrows still move on.
    /// </summary>
    [Parameter]
    public bool ShowSkip { get; set; } = true;

    /// <summary>The queue as a list, shown beside the card on a wide screen; null for none.</summary>
    [Parameter]
    public RenderFragment? QueueList { get; set; }

    /// <summary>One card back (-1) or on (1): by the arrows, Skip, a swipe or the arrow keys.</summary>
    [Parameter]
    public EventCallback<int> OnStep { get; set; }

    /// <summary>A key the deck does not use itself: "1" to "4" (the answers, in their order) and "u" (undo).</summary>
    [Parameter]
    public EventCallback<string> OnKey { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    /// <summary>A swipe across the card: to the left is the next card, to the right the previous one.</summary>
    /// <param name="by">1 or -1.</param>
    /// <returns>A task that ends when the caller has turned the card.</returns>
    [JSInvokable]
    public Task OnSwipe(int by) => OnStep.InvokeAsync(by);

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

    private Task OnShortcutAsync(string key) =>
        key is "ArrowLeft" or "ArrowRight" ? OnStep.InvokeAsync(key == "ArrowLeft" ? -1 : 1) : OnKey.InvokeAsync(key);
}
