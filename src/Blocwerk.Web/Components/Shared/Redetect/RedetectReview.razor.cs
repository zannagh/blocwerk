// <copyright file="RedetectReview.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Redetect;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.Redetect;

/// <summary>The answers of one re-detect review: which proposals are accepted, which are kept, which cards are shown.</summary>
public partial class RedetectReview
{
    private readonly Dictionary<string, bool> answers = [];
    private List<RedetectCard> cards = [];
    private List<RedetectCard> shown = [];
    private RedetectFilter filter = RedetectFilter.All;
    private PanelRedetectPlan? built;
    private int index;

    /// <summary>Gets or sets the planned re-detect.</summary>
    [Parameter]
    [EditorRequired]
    public PanelRedetectPlan Plan { get; set; } = default!;

    /// <summary>Gets or sets the address of the panel's photo.</summary>
    [Parameter]
    [EditorRequired]
    public string PhotoUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the answers are being applied.</summary>
    [Parameter]
    public bool Busy { get; set; }

    /// <summary>Gets or sets what applies the accepted proposals (their keys).</summary>
    [Parameter]
    public EventCallback<IReadOnlySet<string>> OnApply { get; set; }

    /// <summary>Gets or sets what closes the review without changing anything.</summary>
    [Parameter]
    public EventCallback OnCancel { get; set; }

    private RedetectCard? Current => index < shown.Count ? shown[index] : null;

    private int Accepted => answers.Count(a => a.Value);

    private int SafeOpen => cards.Count(c => c.Proposal.Safe && !answers.ContainsKey(c.Proposal.Key));

    /// <summary>Gets the chips: every kind of card that occurs, with how many there are.</summary>
    private IEnumerable<(RedetectFilter Filter, string Label, int Count)> Chips =>
        Enum.GetValues<RedetectFilter>()
            .Select(f => (Filter: f, Label: RedetectCopy.FilterLabel(f), Count: cards.Count(c => RedetectFilters.Matches(f, c.Proposal, Answer(c)))))
            .Where(c => c.Filter == RedetectFilter.All || c.Count > 0);

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(built, Plan))
        {
            built = Plan;
            cards = RedetectCard.Build(Plan, Plan.PhotoAspect);
            answers.Clear();

            // Where the detector is unsure the hold stays as it is, and a new hold is not added, unless a person accepts the card.
            foreach (var card in cards.Where(c => RedetectFilters.StartsKept(c.Proposal)))
            {
                answers[card.Proposal.Key] = false;
            }

            Show(RedetectFilter.All);
        }
    }

    private bool? Answer(RedetectCard card) => answers.TryGetValue(card.Proposal.Key, out var given) ? given : null;

    /// <summary>Shows the cards of one kind, from the first that has no answer yet.</summary>
    private void Show(RedetectFilter chosen)
    {
        filter = chosen;
        shown = [.. cards.Where(c => RedetectFilters.Matches(chosen, c.Proposal, Answer(c)))];
        int open = shown.FindIndex(c => !answers.ContainsKey(c.Proposal.Key));
        index = Math.Max(0, open);
    }

    private void Step(int by)
    {
        if (shown.Count > 0)
        {
            index = (((index + by) % shown.Count) + shown.Count) % shown.Count;
        }
    }

    /// <summary>Records the answer and moves on to the next shown card that has none.</summary>
    private void Give(RedetectCard card, bool accept)
    {
        answers[card.Proposal.Key] = accept;
        for (int step = 1; step <= shown.Count; step++)
        {
            int next = (index + step) % shown.Count;
            if (!answers.ContainsKey(shown[next].Proposal.Key))
            {
                index = next;
                return;
            }
        }
    }

    private void AcceptSafe()
    {
        foreach (var card in cards.Where(c => c.Proposal.Safe && !answers.ContainsKey(c.Proposal.Key)))
        {
            answers[card.Proposal.Key] = true;
        }

        // On to what still needs an answer.
        Show(RedetectFilter.Open);
    }

    private void OnKey(string key)
    {
        if (!Busy && Current is { } card && key is "1" or "2")
        {
            Give(card, key == "2");
        }
    }

    private Task Apply() => OnApply.InvokeAsync(answers.Where(a => a.Value).Select(a => a.Key).ToHashSet());
}
