// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The reviewed classification of every <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> on a feed-covered entity (design §5.3,
/// B2; PR 1.3), checked by <see cref="FeedBulkSiteGuardTests"/>. A site that changes what a viewer sees appends its rows in
/// the write's transaction (<see cref="FeedBulkHandling.Rows"/> or <see cref="FeedBulkHandling.Resync"/>, through
/// <c>FeedBulkWrite</c>, <c>FeedVideoBulk</c> or <c>FeedAccountRows</c>); every other site says why nobody sees it.
/// </summary>
internal static class FeedBulkSites
{
    private const string Core = "Blocwerk.Core/";
    private const string Erase = Core + "Services/AccountDeletionService.Erase.cs";
    private const string Merge = Core + "Services/AccountMergeService.cs";
    private const string ErasedWalls = "the erasure's walls each get a wall.resync in its transaction (FeedAccountRows.EraseAsync)";
    private const string MergedWalls = "the source's walls each get a wall.resync in the merge's transaction (FeedAccountRows.MergeAsync)";
    private const string Quarantined = "Assets/Resolution/AssetHashQuarantine.cs";
    private const string QuarantineReason = "a mismatched hash cleared WHERE hash = the failed sha: wall.manifest in the clear's transaction";
    private const string GpuQueue = "GPU job queue bookkeeping (claim, lease, preview, result); progress goes through IWallPulse and results land on mapped rows";

    /// <summary>
    /// Gets the entities that are not wall-scoped but whose bulk writes still reach the feed as rows for one user (an
    /// external ascent: <c>ticks.resync</c>), so the guard scans them too.
    /// </summary>
    public static IReadOnlySet<Type> UserScoped { get; } = new HashSet<Type> { typeof(ExternalAscent) };

    /// <summary>Gets the calls a handled site's class must make (in the file or a sibling partial).</summary>
    public static IReadOnlyList<string> Markers { get; } = ["FeedBulkWrite.", "FeedVideoBulk.", "FeedAccountRows."];

    /// <summary>Gets every classified site.</summary>
    public static IReadOnlyList<FeedBulkSite> All { get; } =
    [
        .. Handled(),
        .. Account(),
        .. Workflow(),
        .. Gpu(),
    ];

    private static IEnumerable<FeedBulkSite> Handled() =>
    [
        Rows("Services/BetaVideoService.cs", typeof(BetaVideo), 1, "admin re-encode queue: video.status per queued clip (FeedVideoBulk.StatusAsync)"),
        Rows("Feed/FeedVideoBulk.cs", typeof(BetaVideo), 1, "the uploader's delete: video.deleted per clip, the clips locked and the delete narrowed to them"),
        Rows("Services/BetaVideoNormalizer.cs", typeof(BetaVideo), 4, "Processing, Ready (file + poster in one transaction) and Failed: video.status"),
        Rows("Services/BetaVideoNormalizationService.cs", typeof(BetaVideo), 1, "startup reset of stale Processing clips to Pending: video.status"),
        Rows("Services/WallPhotoPrivacyService.Slots.cs", typeof(WallImage), 2, "gallery rewrite and size/hash repair: gallery.changed in the rewrite's transaction"),
        Rows("Services/WallPhotoPrivacyService.Slots.cs", null, 1, "photo columns (wall, panel, reset; live and staged): wall.manifest per rewrite, Setters for staged"),
        Rows("Capture/Retention/SupersededModelRetention.cs", typeof(WallGeometryModel), 1, "retired models stripped: view3d.changed in the strip's transaction"),
        Rows("LookOver/WallLookOverService.Missing.cs", typeof(Hold), 1, "Hold.NeedsReview after a tap-move: hold.changed, in one transaction with the settle"),
        Excluded("Services/HoldTexturePlacementService.EditedWrite.cs", typeof(Hold), 1, "a no-op row lock (FacetId = FacetId) that guards the edited write"),
        Excluded("Data/LinkedHoldSyncStartup.cs", typeof(Wall), 1, "Wall.LinkedHoldSyncVersion only (an unseen column); the hold writes before it are SaveChanges and mapped"),
        Excluded("Feed/WallChangeRetention.cs", null, 1, "the feed's own retention of WallChange rows"),
        Resync("Services/TopLogger/TopLoggerImportService.cs", typeof(ExternalAscent), 2, "grade mapping and disconnect: ticks.resync to the user"),
        .. Quarantine(),
    ];

    private static IEnumerable<FeedBulkSite> Quarantine() =>
    [
        Rows(Quarantined, typeof(WallPanel), 1, QuarantineReason),
        Rows(Quarantined, typeof(Wall), 1, QuarantineReason),
        Rows(Quarantined, typeof(WallReset), 1, QuarantineReason),
        Rows(Quarantined, typeof(BetaVideo), 1, QuarantineReason),
        Rows(Quarantined, typeof(WallImage), 1, QuarantineReason),
        Rows(Quarantined, typeof(WallGeometryTexture), 2, QuarantineReason + " (texture and mask)"),
        Rows(Quarantined, typeof(WallGeometrySplat), 3, QuarantineReason + " (full scene, mobile copy, ladder level)"),
    ];

    private static IEnumerable<FeedBulkSite> Account() =>
    [
        Resync(Erase, typeof(ActivityLogEntry), 1, "user-typed log details blanked; " + ErasedWalls),
        Resync(Erase, typeof(Attempt), 1, "tick notes blanked; " + ErasedWalls),
        Resync(Erase, typeof(BetaVideo), 1, "the user's clips deleted; " + ErasedWalls),
        Resync(Erase, typeof(WallMember), 1, "memberships deleted; " + ErasedWalls + "; their devices reload and lose the wall"),
        Resync(Erase, typeof(ExternalAscent), 1, "imported ascents deleted; the erasure scrubs every row addressed to the user"),
        Resync(Erase, typeof(BoulderFavorite), 1, "user-private (favorite.changed is User); the erasure scrubs every row addressed to the user"),
        Excluded(Erase, typeof(Activity), 1, "user-private training activity (FeedCoverage: excluded)"),
        Excluded(Erase, typeof(ClimbingSession), 1, "user-private check-ins (FeedCoverage: excluded)"),
        Excluded(Erase, typeof(ApiKey), 2, "credentials: wall keys handed to the owner, user keys deleted; no wall view shows them"),
        Resync(Core + "Services/AccountDeletionService.Ownership.cs", typeof(Wall), 2, "ownership transfer and maintenance release; " + ErasedWalls),
        Resync(Merge, typeof(Attempt), 1, MergedWalls),
        Resync(Merge, typeof(ActivityLogEntry), 1, MergedWalls),
        Resync(Merge, typeof(BoulderComment), 1, MergedWalls),
        Resync(Merge, typeof(Boulder), 1, MergedWalls),
        Resync(Merge, typeof(GradeProposal), 1, MergedWalls),
        Resync(Merge, typeof(BetaVideo), 1, MergedWalls),
        Resync(Merge, typeof(HoldLink), 1, MergedWalls),
        Resync(Merge, typeof(Wall), 2, "owner and staging stamp; " + MergedWalls),
        Resync(Merge, typeof(WallPanel), 1, "staging stamp; " + MergedWalls),
        Resync(Merge, typeof(WallReset), 1, MergedWalls),
        Excluded(Merge, typeof(Activity), 1, "user-private training activity (FeedCoverage: excluded)"),
        Excluded(Merge, typeof(ClimbingSession), 1, "user-private check-ins (FeedCoverage: excluded)"),
        Excluded(Merge, typeof(ApiKey), 1, "credentials; no wall view shows them"),
        Excluded("Services/ApiKeyService.Validation.cs", typeof(ApiKey), 1, "LastUsedAt stamp on validation"),
    ];

    private static IEnumerable<FeedBulkSite> Workflow() =>
    [
        Excluded("Capture/FollowUp/CaptureFollowUpRecordStore.cs", typeof(WallCapture), 1, "capture follow-up record (unpublished capture workflow)"),
        Excluded("Capture/WallCaptureProcessor.cs", typeof(WallCapture), 1, "capture retry counter (unpublished capture workflow)"),
        Excluded("Data/CaptureTimelineMerge.cs", typeof(WallCapture), 2, "capture timeline (unpublished capture workflow)"),
        Excluded("HoldLinks/HoldLinkSuggestionService.cs", typeof(HoldLinkSuggestion), 1, "pending link suggestions, an editor workflow; accepting one saves HoldLink rows"),
        Excluded("LookOver/WallLookOverService.Answers.cs", typeof(WallLookOverFinding), 3, "look-over answer claims (update-procedure workflow)"),
        Excluded("LookOver/WallLookOverService.Missing.cs", typeof(WallLookOverFinding), 2, "look-over claim and release (update-procedure workflow)"),
        Excluded("LookOver/WallLookOverService.Settle.cs", typeof(WallLookOverFinding), 1, "look-over settle claim (update-procedure workflow)"),
        Excluded("Refresh/Review/UpdateReviewService.Answers.cs", typeof(WallRefresh), 1, "refresh summary request stamp (update procedure)"),
        Excluded("Refresh/WallRefreshProcessor.Resummarize.cs", typeof(WallRefresh), 1, "refresh summary request cleared (update procedure)"),
        Excluded("Refresh/Review/UpdateReviewService.HoldAnswers.cs", typeof(WallUpdateSession), 1, "update-session activity stamp"),
        Excluded("Services/WallPanelService.SessionStagedEdit.cs", typeof(WallUpdateSession), 1, "update-session activity stamp on a staged edit"),
        Excluded("Services/WallUpdateSessionService.Defaults.cs", typeof(WallUpdateSession), 1, "update-session row lock (UpdatedAt = UpdatedAt)"),
        Excluded("Services/WallUpdateSessionService.Relocations.cs", typeof(WallUpdateSession), 1, "update-session activity stamp"),
    ];

    private static IEnumerable<FeedBulkSite> Gpu() =>
    [
        Excluded("Runners/GpuJobQueue.cs", typeof(GpuJob), 1, GpuQueue),
        Excluded("Runners/GpuJobQueue.Claim.cs", typeof(GpuJob), 1, GpuQueue),
        Excluded("Runners/GpuJobQueue.Holds.cs", typeof(GpuJob), 2, GpuQueue),
        Excluded("Runners/GpuJobQueue.LeftoverRetention.cs", typeof(GpuJob), 1, GpuQueue),
        Excluded("Runners/GpuJobQueue.Preview.cs", typeof(GpuJob), 2, GpuQueue),
        Excluded("Runners/GpuJobQueue.PreviewInstall.cs", typeof(GpuJob), 2, GpuQueue),
        Excluded("Runners/GpuJobQueue.Reattach.cs", typeof(GpuJob), 1, GpuQueue),
        Excluded("Runners/GpuJobQueue.Release.cs", typeof(GpuJob), 1, GpuQueue),
        Excluded("Runners/GpuJobQueue.Report.cs", typeof(GpuJob), 1, GpuQueue),
        Excluded("Runners/GpuJobQueue.Result.cs", typeof(GpuJob), 1, GpuQueue),
        Excluded("Runners/GpuJobQueue.Sweep.cs", typeof(GpuJob), 2, GpuQueue),
        Excluded("Runners/GpuJobQueue.Textures.cs", typeof(GpuJob), 1, GpuQueue),
        Excluded("Runners/GpuJobQueue.Failures.cs", typeof(GpuRunnerFailure), 1, "runner failure retention, runner administration"),
    ];

    private static FeedBulkSite Rows(string file, Type? entity, int count, string reason) => Site(file, entity, count, FeedBulkHandling.Rows, reason);

    private static FeedBulkSite Resync(string file, Type? entity, int count, string reason) => Site(file, entity, count, FeedBulkHandling.Resync, reason);

    private static FeedBulkSite Excluded(string file, Type? entity, int count, string reason) => Site(file, entity, count, FeedBulkHandling.Excluded, reason);

    // Files are named relative to Blocwerk.Core/, unless they already carry the prefix.
    private static FeedBulkSite Site(string file, Type? entity, int count, FeedBulkHandling handling, string reason) =>
        new(file.StartsWith(Core, StringComparison.Ordinal) ? file : Core + file, entity, count, handling, reason);
}
