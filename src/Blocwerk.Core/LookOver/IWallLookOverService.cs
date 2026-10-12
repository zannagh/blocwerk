// <copyright file="IWallLookOverService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "Look over the wall": what an applied panel update was unsure about and nobody answered stays with the wall, the
/// wall is flagged, and a wall admin or moderator works through the findings afterwards. Reading the state is open to
/// every member of the wall; the findings, the answers and finishing are for the wall's owner, admins and moderators
/// (and an installation admin), never from a kiosk tablet.
/// </summary>
public interface IWallLookOverService
{
    /// <summary>Whether the wall waits for a look-over, and how much is open. Null when the user cannot see the wall.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    Task<WallLookOverState?> GetStateAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The findings that are open, and those answered since the wall was flagged, in the order they are worked through.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The findings.</returns>
    Task<IReadOnlyList<LookOverFindingView>> ListAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Answers one open finding; refuses an answer that does not fit it (<see cref="LookOverFindingView.Answers"/>).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingId">The finding.</param>
    /// <param name="answer">The answer.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task AnswerAsync(Guid wallId, Guid findingId, LookOverAnswer answer, CancellationToken ct = default);

    /// <summary>Settles every open unsure match as "same hold". Nothing on the wall changes.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were settled.</returns>
    Task<int> SettleUnsureMatchesAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Finishes the look-over: records who and when and takes the flag off the wall. Refused while findings are open
    /// unless <paramref name="leaveOpen"/> says so (they stay open and listed). Boulders marked for review stay marked.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="leaveOpen">Finish although findings are open.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task FinishAsync(Guid wallId, bool leaveOpen, CancellationToken ct = default);

    /// <summary>One picture of a finding (the old photo, the new photo or the 3D texture), or null when there is none.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingId">The finding.</param>
    /// <param name="view">Which picture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>JPEG bytes.</returns>
    Task<byte[]?> GetCropAsync(Guid wallId, Guid findingId, CheckCropView view, CancellationToken ct = default);
}

/// <summary>The wall's look-over flag, as the wall page and scripts read it.</summary>
/// <param name="Pending">The wall waits for a look-over.</param>
/// <param name="PendingSince">Since when (the update that set the flag).</param>
/// <param name="Generation">The wall generation that update produced.</param>
/// <param name="OpenFindings">Findings nobody answered yet.</param>
/// <param name="TotalFindings">All findings of the updates since the last finished look-over.</param>
/// <param name="CompletedAt">When the last look-over was finished.</param>
/// <param name="CompletedBy">Who finished it.</param>
/// <param name="CanLookOver">The user may open the look-over (owner, wall admin, moderator, installation admin).</param>
public sealed record WallLookOverState(
    bool Pending,
    DateTimeOffset? PendingSince,
    int? Generation,
    int OpenFindings,
    int TotalFindings,
    DateTimeOffset? CompletedAt,
    string? CompletedBy,
    bool CanLookOver);

/// <summary>A boulder that uses a finding's hold.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="NeedsReview">It is marked for review.</param>
public sealed record LookOverBoulder(Guid Id, string Name, bool NeedsReview);

/// <summary>One finding, as the look-over page and the API show it.</summary>
/// <param name="Id">The finding.</param>
/// <param name="Kind">Why it needs a look.</param>
/// <param name="Status">Open, settled or superseded.</param>
/// <param name="Answer">The answer that settled it.</param>
/// <param name="Generation">The wall generation of the update it came from.</param>
/// <param name="OldHoldId">The hold before the update.</param>
/// <param name="HoldId">The live hold it is about (null: a detection that was left out, or a hold deleted since).</param>
/// <param name="OtherHoldId">The new hold on the same spot, while it exists.</param>
/// <param name="HoldName">The hold's name, else its colour.</param>
/// <param name="HasOld">There is an old photo of the hold.</param>
/// <param name="HasModel">There may be a 3D texture picture.</param>
/// <param name="PhotoScore">How well the new photo matches the old one at the spot (-1..1).</param>
/// <param name="Confidence">The matcher's confidence (unsure match).</param>
/// <param name="Converted">An unsure match a person called a different hold.</param>
/// <param name="Boulders">The current boulders that use the hold.</param>
/// <param name="Answers">The answers it takes now (empty once it is not open).</param>
/// <param name="MergeNeedsAdmin">A new hold sits on the spot, but merging holds is for wall admins and the user is a moderator.</param>
public sealed record LookOverFindingView(
    Guid Id,
    LookOverFindingKind Kind,
    LookOverFindingStatus Status,
    LookOverAnswer? Answer,
    int Generation,
    Guid? OldHoldId,
    Guid? HoldId,
    Guid? OtherHoldId,
    string? HoldName,
    bool HasOld,
    bool HasModel,
    double? PhotoScore,
    double? Confidence,
    bool Converted,
    IReadOnlyList<LookOverBoulder> Boulders,
    IReadOnlyList<LookOverAnswer> Answers,
    bool MergeNeedsAdmin = false);
