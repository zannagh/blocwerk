// <copyright file="RefreshConfirm.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>
/// Code-behind of the check step: the cards of the run, which one the deck shows, and the answers. An answer is
/// written through <see cref="IWallRefreshService.DecideCheckAsync"/>, which has the summary made again; the page
/// then shows the run as it is. One answer is sent at a time; the next card can be answered while the summary of the
/// last answer is still being made (Apply waits for it).
/// </summary>
public partial class RefreshConfirm
{
    private readonly DeckCursor cursor = new();
    private IReadOnlyList<RefreshCheck> checks = [];
    private RefreshCheckCard? card;
    private Guid? lastId;
    private bool busy;
    private string? error;

    [Parameter]
    public WallRefreshView View { get; set; } = default!;

    /// <summary>Show everything as lists instead of the card deck.</summary>
    [Parameter]
    public bool ListView { get; set; }

    [Parameter]
    public EventCallback OnApply { get; set; }

    /// <summary>Raised after an answer was written, so the page shows the summary that is being made again.</summary>
    [Parameter]
    public EventCallback OnAnswered { get; set; }

    [Inject]
    private IWallRefreshService Refreshes { get; set; } = default!;

    private IReadOnlyList<RefreshCheck> Queue => RefreshFlow.Queue(checks);

    private RefreshCheck? Current => cursor.Current is { } id ? checks.FirstOrDefault(c => c.Id == id) : null;

    private RefreshCheck? LastAnswered =>
        lastId is { } id ? checks.FirstOrDefault(c => c.Id == id && c.Status != UpdateExceptionStatus.Pending) : null;

    private bool AnswersOff => busy || View.Check3DPending;

    protected override async Task OnParametersSetAsync()
    {
        checks = await Refreshes.GetChecksAsync(View.Id);
        cursor.Sync([.. Queue.Select(c => c.Id)]);
    }

    private static string ApplyNote(RefreshSummary summary)
    {
        if (summary.FullRevision is not null)
        {
            return "Nothing is live yet. Applying cannot be undone from the app.";
        }

        return summary.ChecksOpen == 0 ? "Nothing is live yet." : $"Nothing is live yet. {PlainCopy.Plural(summary.ChecksOpen, "card")} still open.";
    }

    private void Step(int by) => cursor.Step(by);

    /// <summary>A number key gives the answer with that number; U takes the last answer back.</summary>
    private async Task OnKeyAsync(string key)
    {
        if (key == "u")
        {
            if (LastAnswered is { } last && !AnswersOff)
            {
                await ChangeLastAsync(last.Id);
            }
        }
        else if (int.TryParse(key, out var number) && card is not null)
        {
            await card.PressAsync(number);
        }
    }

    /// <summary>Takes the last answer back and turns to that card again.</summary>
    private Task ChangeLastAsync(Guid checkId)
    {
        cursor.ReturnTo(checkId);
        return AnswerAsync(new RefreshCheckAnswer(checkId, UpdateExceptionAnswer.Undo));
    }

    private async Task AnswerAsync(RefreshCheckAnswer answer)
    {
        if (busy)
        {
            return;
        }

        (busy, error) = (true, null);
        lastId = answer.Answer == UpdateExceptionAnswer.Undo ? null : answer.CheckId;
        try
        {
            await Refreshes.DecideCheckAsync(View.Id, answer.CheckId, answer.Answer);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UserFacingException)
        {
            error = ex.Message;
        }
        finally
        {
            busy = false;
        }

        await OnAnswered.InvokeAsync();
    }
}
