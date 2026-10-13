// <copyright file="IPanelRedetectService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Redetect;

/// <summary>
/// Re-detect of one live panel photo from the wall editor, in two steps so the person sees what will happen first.
/// <see cref="PlanAsync"/> runs the detector and changes nothing; <see cref="ApplyAsync"/> writes. A detection that is a
/// stored hold gives that hold its shape and leaves everything else of it alone; stored holds that are the same hold
/// twice are merged like in the duplicate review; only the rest becomes new holds. Wall editors (owner, admin,
/// moderator); merging needs a wall admin, for anybody else sure pairs are only left for the duplicate review.
/// </summary>
public interface IPanelRedetectService
{
    /// <summary>Runs the detector on the panel's live photo and works out what applying it would do.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="panelId">The panel being edited.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan; empty for a panel without a live photo.</returns>
    Task<PanelRedetectPlan> PlanAsync(Guid wallId, Guid panelId, CancellationToken ct = default);

    /// <summary>
    /// Applies a plan against the holds as they are now (the decisions are worked out again, so an edit made in between
    /// is respected). Shapes, new holds and removals are one journal batch; every merge is its own batch, undoable in
    /// the duplicate review.
    /// </summary>
    /// <param name="plan">The plan of <see cref="PlanAsync"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was done.</returns>
    Task<PanelRedetectSummary> ApplyAsync(PanelRedetectPlan plan, CancellationToken ct = default);
}

/// <summary>A planned re-detect: the detections, and what applying them would do right now.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="PanelId">The panel.</param>
/// <param name="Detections">The fresh detections with their outlines.</param>
/// <param name="Summary">What applying would do.</param>
public sealed record PanelRedetectPlan(
    Guid WallId, Guid PanelId, IReadOnlyList<RedetectDetection> Detections, PanelRedetectSummary Summary);

/// <summary>One pair of stored holds a re-detect merges.</summary>
/// <param name="KeptId">The hold that stays.</param>
/// <param name="RemovedId">The hold that goes.</param>
/// <param name="Name">The name of the hold that stays (or of the other one when it has none).</param>
/// <param name="Color">The colour of the hold that stays (or of the other one when it has none).</param>
/// <param name="MovedBoulders">Boulders that move from the removed hold to the kept one.</param>
/// <param name="BatchId">The merge's journal batch once it is done.</param>
public sealed record RedetectMergeInfo(Guid KeptId, Guid RemovedId, string? Name, string? Color, int MovedBoulders, Guid? BatchId = null);

/// <summary>What a re-detect would do, or did.</summary>
/// <param name="Detected">Holds the detector found on the photo.</param>
/// <param name="BetterShapes">Stored holds that take the detection's position and outline.</param>
/// <param name="FilledOutlines">Hand-placed holds without an outline that get one.</param>
/// <param name="NewHolds">Detections that become new holds.</param>
/// <param name="Merges">Stored pairs that are merged.</param>
/// <param name="ForReview">Stored pairs that look alike and are left for the duplicate review.</param>
/// <param name="Removed">Automatic holds that were not found again, which no boulder uses, that are removed.</param>
/// <param name="HandShapesKept">Matched holds whose hand-made shape stays untouched.</param>
/// <param name="Skipped">Detections on a spot that is taken, which are not added.</param>
/// <param name="FailedMerges">Merges that could not be done when applying.</param>
public sealed record PanelRedetectSummary(
    int Detected,
    int BetterShapes,
    int FilledOutlines,
    int NewHolds,
    IReadOnlyList<RedetectMergeInfo> Merges,
    int ForReview,
    int Removed,
    int HandShapesKept,
    int Skipped,
    int FailedMerges = 0)
{
    /// <summary>Gets a summary of a run that found and changes nothing.</summary>
    public static PanelRedetectSummary Nothing { get; } = new(0, 0, 0, 0, [], 0, 0, 0, 0);

    /// <summary>Gets a value indicating whether applying changes anything.</summary>
    public bool Any => BetterShapes + FilledOutlines + NewHolds + Merges.Count + Removed > 0;
}
