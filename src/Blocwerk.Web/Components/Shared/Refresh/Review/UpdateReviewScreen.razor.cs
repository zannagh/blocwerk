// <copyright file="UpdateReviewScreen.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Refresh.Review;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.Refresh.Review;

/// <summary>
/// Code-behind of the one review: which panel and group are shown, the deck's queue (the unanswered items of that group)
/// and the counts. Every list is the server's items with the answers given on this page laid over them until the server
/// has them (<see cref="overlay"/>), so the counts move with every tap.
/// </summary>
public partial class UpdateReviewScreen : IDisposable
{
    private readonly Dictionary<string, ReviewAnswer?> overlay = new(StringComparer.Ordinal);
    private Core.Refresh.Review.UpdateReview? review;
    private IReadOnlyList<ReviewItem>? items;
    private ReviewItemCard? card;
    private ReviewGroup? group;
    private Guid? panel;
    private string? currentKey;
    private string? lastKey;
    private string? error;
    private (string? Version, bool Pending, bool Updating)? shownState;

    [Parameter]
    [EditorRequired]
    public WallRefreshView View { get; set; } = default!;

    /// <summary>Show the group as a list instead of the card deck.</summary>
    [Parameter]
    public bool ListView { get; set; }

    [Parameter]
    public EventCallback OnApply { get; set; }

    /// <summary>Raised once the answers sent so far are written, so the page reads the run again (Apply's summary).</summary>
    [Parameter]
    public EventCallback OnAnswered { get; set; }

    [Inject]
    private IUpdateReviewService Reviews { get; set; } = default!;

    [Inject]
    private Microsoft.JSInterop.IJSRuntime JS { get; set; } = default!;

    /// <summary>The server's items with this page's answers laid over them; made again whenever either changes.</summary>
    private IReadOnlyList<ReviewItem> Items => items ??= review is null ? [] : review.Items.Select(Overlaid).ToList();

    private ReviewCounts Counts => ReviewCounts.Of(Items.ToList());

    private IReadOnlyList<ReviewGroupCount> Groups => ReviewCounts.Of(Items.Where(OnPanel).ToList()).Groups;

    private IReadOnlyList<ReviewItem> InGroup => Items.Where(i => i.Group == group && OnPanel(i)).ToList();

    private IReadOnlyList<ReviewItem> Queue => InGroup.Where(i => !i.IsAnswered).ToList();

    private int Index => Math.Max(0, Queue.ToList().FindIndex(i => i.Key == currentKey));

    private ReviewItem? Current => Queue.FirstOrDefault(i => i.Key == currentKey) ?? Queue.FirstOrDefault();

    private ReviewItem? LastAnswered => lastKey is { } key ? Items.FirstOrDefault(i => i.Key == key && i.IsAnswered) : null;

    private string EmptyText => Groups.All(g => g.ToCheck == 0)
        ? "Everything is answered. Apply when you are ready."
        : "Nothing left to check here. Pick another group above.";

    public void Dispose() => writes.Dispose();

    protected override async Task OnParametersSetAsync()
    {
        var state = (View.Summary?.DecisionsVersion, View.Check3DPending, View.SummaryUpdating);
        if (shownState == state || Writing)
        {
            return;
        }

        shownState = state;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            review = await Reviews.GetAsync(View.Id);
            items = null;
            group ??= FirstOpenGroup();
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
    }

    private ReviewGroup? FirstOpenGroup()
    {
        var groups = Groups;
        return groups.FirstOrDefault(g => g.Open > 0)?.Group ?? groups.FirstOrDefault(g => g.ToCheck > 0)?.Group ?? groups.FirstOrDefault()?.Group;
    }

    private bool OnPanel(ReviewItem item) =>
        panel is null || item.PanelId == panel || (item.PanelId is null && review?.Panels.FirstOrDefault()?.PanelId == panel);

    private string ToCheckOn(Guid panelId) => Items.Count(i => i.PanelId == panelId && !i.IsAnswered).ToString();

    private ReviewItem Overlaid(ReviewItem item) =>
        overlay.TryGetValue(item.Key, out var answer) ? item with { Answer = answer, AnsweredBy = answer is null ? null : Saving } : item;

    private void PickPanel(Guid? id)
    {
        panel = id;
        currentKey = null;
        if (group is { } g && !Groups.Any(c => c.Group == g))
        {
            group = FirstOpenGroup();
        }
    }

    private void PickGroup(ReviewGroup picked) => (group, currentKey) = (picked, null);

    private void Step(int by)
    {
        var queue = Queue;
        if (queue.Count == 0)
        {
            return;
        }

        var next = (Index + by + queue.Count) % queue.Count;
        currentKey = queue[next].Key;
    }

    /// <summary>A number key gives the answer with that number; U takes the last answer back.</summary>
    private async Task OnKeyAsync(string key)
    {
        if (key == "u")
        {
            if (LastAnswered is { } last)
            {
                await AnswerAsync(last, ReviewAnswer.Undo);
            }
        }
        else if (int.TryParse(key, out var number) && card is not null)
        {
            await card.PressAsync(number);
        }
    }
}
