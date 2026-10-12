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
    /// <param name="expectedBoulderIds">
    /// The boulders the user was shown on the hold. Required for every answer but <see cref="LookOverAnswer.Settle"/>:
    /// when other boulders use the hold by now, the answer is refused and the user looks again.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task AnswerAsync(
        Guid wallId, Guid findingId, LookOverAnswer answer, IReadOnlyCollection<Guid>? expectedBoulderIds = null, CancellationToken ct = default);

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
