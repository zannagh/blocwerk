// <copyright file="HoldLookModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>A carried pair whose look is not clearly the same.</summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="NewHoldId">The staged detection the matcher paired it with.</param>
/// <param name="PanelId">The staged panel of the detection.</param>
/// <param name="Look">Unsure (still paired) or replaced (no longer paired).</param>
/// <param name="Score">The patch correlation; null for a pair kept as replaced from an earlier run that is not measurable now.</param>
public sealed record HoldLookFinding(Guid OldHoldId, Guid NewHoldId, Guid PanelId, HoldLook Look, double? Score);

/// <summary>What the look check reads besides the pairs: the old holds and photos per panel, and the panels not aligned.</summary>
/// <param name="OldByPosition">The carried old holds per grid position.</param>
/// <param name="OldPanelPhotosById">The live photo of each re-shot panel.</param>
/// <param name="OldCentrePhoto">The photo the old centre holds are normalized to.</param>
/// <param name="UnalignedPanelIds">Staged panels whose photo could not be lined up: nothing on them is judged.</param>
internal sealed record LookInputs(
    IReadOnlyDictionary<(int Col, int Row), List<Hold>> OldByPosition,
    IReadOnlyDictionary<Guid, byte[]> OldPanelPhotosById,
    byte[]? OldCentrePhoto,
    IReadOnlySet<Guid> UnalignedPanelIds);

/// <summary>One re-photographed, aligned panel as the look check reads it.</summary>
/// <param name="PanelId">The staged panel.</param>
/// <param name="Col">Its grid column.</param>
/// <param name="Row">Its grid row.</param>
/// <param name="OldHolds">The old holds carried on this panel.</param>
/// <param name="Staged">The staged detections of the panel.</param>
/// <param name="OldPhoto">The photo the old holds are normalized to.</param>
/// <param name="NewPhoto">The staged photo.</param>
internal sealed record LookPanel(Guid PanelId, int Col, int Row, IReadOnlyList<Hold> OldHolds, IReadOnlyList<Hold> Staged, byte[] OldPhoto, byte[] NewPhoto);

/// <summary>A panel's geometry for building probe queries (RAW pixels of both photos).</summary>
/// <param name="Panel">The panel.</param>
/// <param name="Old">Its old holds that have pixels of their own (not virtual), by id.</param>
/// <param name="Staged">Its staged detections by id.</param>
/// <param name="NewSize">The staged photo's size.</param>
/// <param name="OldSize">The old photo's size.</param>
/// <param name="Warp">The matcher's predicted spot of each old hold on the staged photo.</param>
/// <param name="NewToOld">The panel's matched pairs, staged photo to old photo.</param>
internal sealed record LookFrame(
    LookPanel Panel,
    IReadOnlyDictionary<Guid, Hold> Old,
    IReadOnlyDictionary<Guid, Hold> Staged,
    (int Width, int Height) NewSize,
    (int Width, int Height) OldSize,
    IReadOnlyDictionary<Guid, HoldPositionNorm> Warp,
    IReadOnlyList<PointPair> NewToOld)
{
    /// <summary>A hold's radius on the staged photo (radii are normalized to the longer side).</summary>
    public double RadiusPx(Hold hold) => hold.Radius * Math.Max(NewSize.Width, NewSize.Height);

    /// <summary>The detection of a pair against the old hold on the old photo.</summary>
    public PresenceQuery? PairQuery(CarryoverProposal pair)
    {
        var (old, twin) = (Old[pair.OldHoldId], Staged[pair.NewHoldId]);
        return HoldLookCheck.Query(
            NewToOld, (twin.X * NewSize.Width, twin.Y * NewSize.Height), OldSpot(old), NewSize, Math.Max(RadiusPx(old), RadiusPx(twin)));
    }

    /// <summary>The predicted spot of an old hold against the old hold on the old photo.</summary>
    public PresenceQuery? SpotQuery(Guid oldHoldId)
    {
        var old = Old[oldHoldId];
        return HoldLookCheck.Query(
            NewToOld, (Warp[oldHoldId].X * NewSize.Width, Warp[oldHoldId].Y * NewSize.Height), OldSpot(old), NewSize, RadiusPx(old));
    }

    /// <summary>
    /// The detection of a pair against where its spot maps to on the old photo by the OTHER pairs (<paramref name="others"/>):
    /// the new-hold triage's question "was this already there?", with the radius the triage asks it with.
    /// </summary>
    public PresenceQuery? UnchangedQuery(CarryoverProposal pair, IReadOnlyList<PointPair> others)
    {
        var twin = Staged[pair.NewHoldId];
        var (x, y) = (twin.X * NewSize.Width, twin.Y * NewSize.Height);
        return LocalAffine.Predict(others, x, y) is { } old && LocalAffine.Scale(others, x, y) is { } scale and > 0
            ? new PresenceQuery(x, y, old.X, old.Y, scale, NewHoldTriage.ProbeRadiusPx(RadiusPx(twin)))
            : null;
    }

    private (double X, double Y) OldSpot(Hold old) => (old.X * OldSize.Width, old.Y * OldSize.Height);
}

/// <summary>One pair as scored and judged.</summary>
/// <param name="Pair">The matcher's proposal.</param>
/// <param name="Panel">Its panel.</param>
/// <param name="Score">The correlation, or null when it could not be measured.</param>
/// <param name="OldRadiusPx">The old hold's radius on the staged photo.</param>
/// <param name="TwinRadiusPx">The detection's radius on the staged photo.</param>
/// <param name="SameKind">The two holds' fingerprints are alike.</param>
internal sealed record JudgedPair(CarryoverProposal Pair, LookPanel Panel, double? Score, double OldRadiusPx, double TwinRadiusPx, bool SameKind)
{
    /// <summary>Gets or sets the verdict; null while the pair is not judged (no score).</summary>
    public HoldLook? Look { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the detection's spot looks unchanged since the old photo although the pair
    /// does not match: the matcher paired the old hold with something that was there before and is not its successor.
    /// Such a pair is taken apart like a replaced one, but nobody is asked about a replacement: the old hold is simply
    /// not found, the detection is the triage's to drop.
    /// </summary>
    public bool Mispaired { get; set; }
}

/// <summary>One old hold's predicted spot as scored.</summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="Panel">Its panel.</param>
/// <param name="Score">The correlation, or null when it could not be measured.</param>
/// <param name="OldRadiusPx">The old hold's radius on the staged photo.</param>
internal sealed record JudgedSpot(Guid OldHoldId, LookPanel Panel, double? Score, double OldRadiusPx);

/// <summary>What the look check of one session found.</summary>
internal sealed class LookOutcome
{
    /// <summary>Gets the pairs that are unsure or replaced.</summary>
    public List<HoldLookFinding> Findings { get; } = [];

    /// <summary>Gets every pair that was looked at, with its score and verdict.</summary>
    public List<JudgedPair> Pairs { get; } = [];

    /// <summary>Gets every predicted spot that was looked at.</summary>
    public List<JudgedSpot> Spots { get; } = [];

    /// <summary>Gets the old holds whose boulders are marked for review, with why (filled by the service).</summary>
    public Dictionary<Guid, HoldMoveOutcome> NotFoundAgain { get; } = [];

    /// <summary>Gets the physical holds (component representatives) that still show on some panel.</summary>
    public HashSet<Guid> Present { get; } = [];

    /// <summary>Gets or sets the component of each linked old hold.</summary>
    public Dictionary<Guid, Guid> Components { get; set; } = [];

    /// <summary>The representative of the physical hold <paramref name="oldHoldId"/> belongs to.</summary>
    public Guid RootOf(Guid oldHoldId) => Components.GetValueOrDefault(oldHoldId, oldHoldId);

    /// <summary>Whether the old hold's own predicted spot confidently no longer shows it, and no linked panel shows it either.</summary>
    public bool IsGone(Guid oldHoldId, HoldMoves.HoldMoveOptions options) =>
        !Present.Contains(RootOf(oldHoldId))
        && Spots.Any(s => s.OldHoldId == oldHoldId && s.Score is { } score && HoldLookCheck.LooksGone(score, options));
}
