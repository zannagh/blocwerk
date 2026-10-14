// <copyright file="RedetectReview.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Redetect;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.Redetect;

/// <summary>The answers of one re-detect review: which proposals are accepted, which are kept, and the card shown.</summary>
public partial class RedetectReview
{
    private readonly Dictionary<string, bool> answers = [];
    private List<RedetectCard> cards = [];
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

    private RedetectCard? Current => index < cards.Count ? cards[index] : null;

    private int Accepted => answers.Count(a => a.Value);

    private int Kept => answers.Count(a => !a.Value);

    private int Open => cards.Count - answers.Count;

    private int SafeOpen => cards.Count(c => c.Proposal.Safe && !answers.ContainsKey(c.Proposal.Key));

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(built, Plan))
        {
            built = Plan;
            cards = RedetectCard.Build(Plan, Plan.PhotoAspect);
            answers.Clear();

            // Where the detector is unsure the hold stays as it is unless a person accepts the card.
            foreach (var card in cards.Where(c => c.Proposal.Unsure))
            {
                answers[card.Proposal.Key] = false;
            }

            index = 0;
        }
    }

    private bool? Answer(RedetectCard card) => answers.TryGetValue(card.Proposal.Key, out var given) ? given : null;

    private void Step(int by)
    {
        if (cards.Count > 0)
        {
            index = (((index + by) % cards.Count) + cards.Count) % cards.Count;
        }
    }

    /// <summary>Records the answer and moves on to the next card that has none.</summary>
    private void Give(RedetectCard card, bool accept)
    {
        answers[card.Proposal.Key] = accept;
        for (int step = 1; step <= cards.Count; step++)
        {
            int next = (index + step) % cards.Count;
            if (!answers.ContainsKey(cards[next].Proposal.Key))
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

        // On to the first card that still needs an answer.
        int open = cards.FindIndex(c => !answers.ContainsKey(c.Proposal.Key));
        index = open < 0 ? index : open;
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
