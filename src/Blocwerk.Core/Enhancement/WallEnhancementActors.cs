// <copyright file="WallEnhancementActors.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.HoldStacks;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// The wall services one enhancement step works through, built for the admin who started the run (the same admin
/// and kiosk checks as in the UI). Disposing it ends the scope they were built in.
/// </summary>
/// <param name="Outlines">Traces real outlines.</param>
/// <param name="Stacks">Stacks holds on holds.</param>
/// <param name="Cleanup">Smooths and separates shapes.</param>
/// <param name="Colours">Suggests colours.</param>
/// <param name="Duplicates">Finds duplicates.</param>
/// <param name="Links">Suggests links across panels.</param>
/// <param name="Proposals">Finds new holds in the capture photos.</param>
/// <param name="Scope">What to dispose with it, if anything.</param>
public sealed record WallEnhancementActors(
    IHoldOutlineUpgradeService Outlines,
    IHoldStackService Stacks,
    IHoldShapeCleanupService Cleanup,
    IHoldColorProposalService Colours,
    IHoldDuplicateService Duplicates,
    IHoldLinkSuggestionService Links,
    IHoldProposalService Proposals,
    IAsyncDisposable? Scope = null) : IAsyncDisposable
{
    /// <inheritdoc/>
    public ValueTask DisposeAsync() => Scope?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>Builds <see cref="WallEnhancementActors"/> for a user (the app: a DI scope per step; tests: their own).</summary>
public interface IWallEnhancementActorFactory
{
    /// <summary>The services acting for the user.</summary>
    /// <param name="userId">The admin who started the run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The services; dispose them after the step.</returns>
    Task<WallEnhancementActors> CreateAsync(Guid userId, CancellationToken ct);
}
