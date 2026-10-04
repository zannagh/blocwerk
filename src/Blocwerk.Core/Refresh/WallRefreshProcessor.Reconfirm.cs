// <copyright file="WallRefreshProcessor.Reconfirm.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Apply found that the update would promote something other than the summary the user confirmed: choices were changed
/// in the full review (by them or another admin) after the summary was made, holds were added or deleted on the new
/// photos, or the old holds matched differently at apply time, so verdicts outside the reviewable panel would be reset.
/// Nothing is promoted: the run goes back to the
/// confirm screen with the summary of what Apply would do now, and says why. Applying again promotes exactly that.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    /// <summary>Shown above the confirm screen when the summary had to be made again.</summary>
    public const string EditedSinceSummary =
        "This update was changed in the full review after the summary was made, so nothing was applied. Check the updated summary, then apply.";

    /// <summary>Shown above the confirm screen when the matcher's result moved between the summary and Apply.</summary>
    public const string MatchedDifferently =
        "The old holds matched differently than when the summary was made, so nothing was applied. Check the updated summary, then apply.";

    /// <summary>
    /// Shown above the confirm screen when the summary was out of date for another reason: holds added or deleted on the
    /// new photos while the update was staged, or a summary made by an earlier version of the app.
    /// </summary>
    public const string OutOfDate =
        "The update changed since the summary was made, so nothing was applied. Check the updated summary, then apply.";

    private async Task ReconfirmAsync(
        WallRefresh refresh, RefreshSummary? summary, PromotableDecisions promotable, WallUpdateSessionInfo open, CancellationToken ct)
    {
        var edited = summary?.DecisionsRecordedAt is { } recorded && open.UpdatedAt > recorded;
        var counts = await SummarizeAsync(
            refresh.WallId, RefreshDecisions.AsQuick(promotable), promotable.PendingRelocations, summary?.Panels ?? [], ct);
        var fresh = counts with
        {
            // The 3D check's numbers describe the triage's suggestions; capped, as the full review may have kept some.
            DroppedByThe3DModel = Math.Min(summary?.DroppedByThe3DModel ?? 0, counts.DroppedDetections),
            NewSeenIn3D = Math.Min(summary?.NewSeenIn3D ?? 0, counts.NewHolds),
            CheckedWithModelId = summary?.CheckedWithModelId,
            Attempted3DModelId = summary?.Attempted3DModelId,
            DecisionsRecordedAt = summary?.DecisionsRecordedAt,
            DecisionsVersion = promotable.Version,
            EditedInFullReview = (summary?.EditedInFullReview ?? false) || edited,
        };
        var message = edited ? EditedSinceSummary : promotable.Resets > 0 ? MatchedDifferently : OutOfDate;
        await SaveAsync(
            refresh,
            r =>
            {
                r.SummaryJson = RefreshTimeline.Write(fresh);
                r.Status = WallRefreshStatus.ReadyToApply;
                r.ConfirmedDecisionsVersion = null;
                r.Error = message;
                RefreshTimeline.Set(r, RefreshTimeline.Apply, RefreshStepState.Pending);
                RefreshTimeline.Set(r, RefreshTimeline.Review, RefreshStepState.Waiting, "Check the updated summary, then apply");
            },
            ct);
        logger.LogInformation(
            "Wall refresh {RefreshId}: not applied, the decisions changed since the summary ({Resets} verdicts reset); summary made again",
            refresh.Id,
            promotable.Resets);
    }
}
