// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The reviewed classification of every wall- or boulder-scoped entity the feed does not map (design §5.3, model coverage
/// guard). An entity with a <c>WallId</c> or <c>BoulderId</c> is either mapped (<see cref="FeedMappers"/>), excluded here
/// with a reason, or pending here with the PR that maps it. The guard test fails for anything else, so a new entity
/// cannot be added without deciding whether viewers see it change.
/// </summary>
public static class FeedCoverage
{
    /// <summary>Gets the entities that are deliberately not in the feed, with why.</summary>
    public static IReadOnlyDictionary<Type, string> ReviewedExcluded { get; } = new Dictionary<Type, string>
    {
        [typeof(ApiKey)] = "credentials, managed on the owner's settings page; no wall view shows them",
        [typeof(GpuJob)] = "job queue (GpuJobQueue); progress goes through IWallPulse, results land on mapped rows",
        [typeof(GpuRunnerApproval)] = "runner administration, not a wall view",
        [typeof(GpuRunnerFailure)] = "runner administration, not a wall view",
        [typeof(GpuRunnerWall)] = "runner administration, not a wall view",
        [typeof(WallCapture)] = "capture workflow (unpublished until promoted; the promote writes mapped rows)",
        [typeof(WallEnhancementRun)] = "enhancement job state; progress goes through IWallPulse",
        [typeof(HoldOutlineUpgradeRun)] = "job state; its outline changes are Hold rows",
        [typeof(HoldPlacementRun)] = "job state; its placements are Hold rows",
        [typeof(WallLookOverFinding)] = "look-over answers and settle (update-procedure workflow)",
        [typeof(WallMarkerPlan)] = "marker planner, an editor tool",
        [typeof(WallRefresh)] = "refresh workflow and summary (update procedure)",
        [typeof(WallUpdateSession)] = "update-session state (update procedure)",
        [typeof(HoldProposal)] = "shape proposals, an editor workflow; accepting one changes Hold rows",
        [typeof(HoldLinkSuggestion)] = "link suggestions, an editor workflow; accepting one writes HoldLink rows",
        [typeof(HoldDuplicateDismissal)] = "editor bookkeeping of dismissed duplicate pairs",
    };

    /// <summary>Gets the user-visible entities whose mapper has not landed yet, with the PR that brings it.</summary>
    public static IReadOnlyDictionary<Type, string> Pending { get; } = new Dictionary<Type, string>
    {
        [typeof(Wall)] = "1.2b: wall.updated (generation, photo, settings)",
        [typeof(WallMember)] = "1.2b: member.changed, access.revoked",
        [typeof(WallSegment)] = "1.2b: segment.changed",
        [typeof(WallPanel)] = "1.2b: panel.changed",
        [typeof(HoldLink)] = "1.2b: link.changed",
        [typeof(WallVolume)] = "1.2b: volume.changed",
        [typeof(Hold)] = "1.2b: hold.changed, hold.deleted",
        [typeof(Boulder)] = "1.2b: boulder.changed, boulder.deleted",
        [typeof(BoulderHold)] = "1.2b: boulder holds, as boulder.changed",
        [typeof(BoulderSetter)] = "1.2b: setters are shown with the boulder, as boulder.changed",
        [typeof(BoulderHoldMove)] = "1.2b: move hints are shown with the boulder, as boulder.changed",
        [typeof(HoldGenerationLink)] = "1.2b: lineage feeds the historic views; map or exclude with the hold family",
        [typeof(WallReset)] = "1.2b: a reset moves the generation, as wall.updated",
        [typeof(WallTemperatureReading)] = "1.2b: not in §5.4; decide (the wall card polls it today)",
        [typeof(BoulderComment)] = "1.2c: comment.added, comment.deleted (with compaction)",
        [typeof(BetaVideo)] = "1.2c: video.added, video.status, video.deleted",
        [typeof(Attempt)] = "1.2c: boulder.stats, tick.mine",
        [typeof(BoulderRating)] = "1.2c: boulder.stats, rating.mine",
        [typeof(GradeProposal)] = "1.2c: boulder.stats",
        [typeof(BoulderFavorite)] = "1.2c: favorite.changed",
        [typeof(WallImage)] = "1.2c: gallery.changed, gallery.deleted",
        [typeof(ActivityLogEntry)] = "1.2c: activity.added",
        [typeof(WallGeometryModel)] = "1.2c: view3d.changed",
        [typeof(User)] = "1.2c: user.profile (in the guard through its home-wall key)",
        [typeof(Activity)] = "1.2c: per-user progression, not in §5.4; decide (likely excluded, derived from attempts)",
        [typeof(ClimbingSession)] = "1.2c: per-user sessions, not in §5.4; decide",
    };
}
