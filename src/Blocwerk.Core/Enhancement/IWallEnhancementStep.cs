// <copyright file="IWallEnhancementStep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// One step of a wall's one-time enhancement (<see cref="WallEnhancementJob"/>), run in <see cref="Order"/>. A step that
/// writes (its <see cref="Kind"/> is a written kind, see <see cref="WallEnhancementKinds.IsWritten"/>) goes through
/// the app's own service for it, inside the step's journal batch; the job then turns every hold the step's batches
/// changed into a review item. A step that suggests writes nothing and returns its suggestions instead.
/// </summary>
public interface IWallEnhancementStep
{
    /// <summary>Stable key recorded on the run (never rename: a restart resumes by it).</summary>
    string Key { get; }

    /// <summary>Position in the chain (ascending).</summary>
    int Order { get; }

    /// <summary>What the step does, for the progress line ("Tracing the real outlines of the holds").</summary>
    string Title { get; }

    /// <summary>What its review items are about.</summary>
    WallEnhancementItemKind Kind { get; }

    /// <summary>True when the step needs the wall's active 3D model; without one it is skipped.</summary>
    bool NeedsModel => false;

    /// <summary>
    /// The labels of the journal batches the step's service opens itself (the clean-up's, the colours'); its writes
    /// outside them land in the step's own batch (<see cref="WallEnhancementKinds.BatchLabel"/>).
    /// </summary>
    IReadOnlyList<string> OwnBatchLabels => [];

    /// <summary>Runs the step. Throwing records it as failed; the chain goes on with the next step.</summary>
    /// <param name="context">The wall and the services acting for the admin who started the run.</param>
    /// <param name="ct">Cancellation (app shutdown: the step runs again on resume).</param>
    /// <returns>The one-line summary and any suggestions.</returns>
    Task<WallEnhancementStepOutcome> RunAsync(WallEnhancementStepContext context, CancellationToken ct);
}
