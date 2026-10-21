// <copyright file="WallEnhancementStepModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enhancement;

/// <summary>What a step runs with.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="UserId">The admin who started the run; the services act for them.</param>
/// <param name="Actors">The wall services, built for that admin.</param>
/// <param name="Progress">Where a long step reports how far it is (photos done of all), for the card and the deploy gate's ETA.</param>
public sealed record WallEnhancementStepContext(
    Guid WallId, Guid UserId, WallEnhancementActors Actors, IProgress<WallEnhancementProgress>? Progress = null);

/// <summary>How far a long step is.</summary>
/// <param name="Done">Units done (photos).</param>
/// <param name="Total">All units.</param>
public readonly record struct WallEnhancementProgress(int Done, int Total);

/// <summary>A suggestion of a suggesting step: what it is about, for the review item.</summary>
/// <param name="HoldId">The hold (the left one of a pair).</param>
/// <param name="OtherHoldId">The other hold of a pair.</param>
/// <param name="ReferenceId">The suggestion's own row (a hold proposal).</param>
/// <param name="Detail">Kind-specific detail (the merge mode).</param>
public sealed record WallEnhancementSuggestion(Guid? HoldId, Guid? OtherHoldId, Guid? ReferenceId, string? Detail);

/// <summary>What a step did.</summary>
/// <param name="Summary">One line for people ("412 holds got their real outline").</param>
/// <param name="Suggestions">The suggestions (suggesting steps only).</param>
/// <param name="Skipped">True when the step could not run here (no 3D model, switched off).</param>
public sealed record WallEnhancementStepOutcome(string Summary, IReadOnlyList<WallEnhancementSuggestion> Suggestions, bool Skipped = false)
{
    /// <summary>A step that ran.</summary>
    /// <param name="summary">Its summary.</param>
    /// <returns>The outcome.</returns>
    public static WallEnhancementStepOutcome Done(string summary) => new(summary, []);

    /// <summary>A step that could not run here.</summary>
    /// <param name="why">Why, for people.</param>
    /// <returns>The outcome.</returns>
    public static WallEnhancementStepOutcome Skip(string why) => new(why, [], Skipped: true);
}
