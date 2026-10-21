// <copyright file="NewHoldsEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Enhancement.Steps;

/// <summary>
/// Step 7 (3D model only): holds the capture photos see that the wall does not have
/// (<see cref="IHoldProposalService.FindInBackgroundAsync"/>, which waits its turn behind another wall's search). Writes
/// only proposal rows; every pending proposal on a panel becomes a review item that adds the hold or turns it down,
/// through the same service as the hold search section.
/// </summary>
public sealed class NewHoldsEnhancementStep : IWallEnhancementStep
{
    /// <inheritdoc/>
    public string Key => "new-holds";

    /// <inheritdoc/>
    public int Order => 700;

    /// <inheritdoc/>
    public string Title => "Finding holds the capture photos show but the wall lacks";

    /// <inheritdoc/>
    public WallEnhancementItemKind Kind => WallEnhancementItemKind.NewHold;

    /// <inheritdoc/>
    public bool NeedsModel => true;

    /// <inheritdoc/>
    public async Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct)
    {
        await context.Actors.Proposals.EnsureCanFindAsync(context.WallId, ct);

        // The active model's own proposal pass (the capture chain's, often by a 3D runner) is reused as it is: a search
        // here would take most of an hour on the server's CPU to find the same holds again, without the runner's detections.
        var reused = await context.Actors.Proposals.HasProposalPassAsync(context.WallId, ct);
        if (!reused)
        {
            var progress = new Progress<HoldSearchProgress>(p => context.Progress?.Report(new WallEnhancementProgress(p.Done, p.Total)));
            await context.Actors.Proposals.FindInBackgroundAsync(context.WallId, progress, ct);
        }

        var pending = await context.Actors.Proposals.ListAsync(context.WallId, ct: ct);
        var suggestions = pending
            .Where(p => p.PanelId is not null && !p.IsStale)
            .Select(p => new WallEnhancementSuggestion(null, null, p.Id, null))
            .ToList();
        return new WallEnhancementStepOutcome(
            suggestions.Count == 0
                ? "No missing holds found."
                : $"{WallEnhancementKinds.Count(suggestions.Count, "possible new hold", "possible new holds")} to look at"
                  + (reused ? " (from the 3D model's own search)." : "."),
            suggestions);
    }
}
