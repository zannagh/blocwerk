// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The rows the bulk sites append (<see cref="FeedBulkWrite"/>), in the shapes the mappers write for the same change, so a
/// consumer cannot tell a bulk write from a tracked one. Ids, enums and flags only (§5.4).
/// </summary>
public static class FeedBulkRows
{
    /// <summary>One actorless <c>wall.resync</c> per wall: it reaches every viewer, so it names nobody.</summary>
    public static IEnumerable<WallChange> Resyncs(IEnumerable<Guid> wallIds, string reason) =>
        wallIds.Distinct().Select(wallId => FeedRows.Resync(wallId, reason, actorUserId: null, DateTimeOffset.UtcNow));

    /// <summary>A <c>video.*</c> row as <c>BetaVideoFeedMapper</c> writes it; a draft's goes to setters only.</summary>
    public static WallChange Video(FeedVideoFacts video, string kind, BetaVideoEncodingStatus status, bool hasHls) => new()
    {
        WallId = video.WallId,
        BoulderId = video.BoulderId,
        Kind = kind,
        EntityId = video.Id,
        Audience = video.IsDraft ? FeedAudience.Setters : FeedAudience.Wall,
        DataJson = FeedJson.Serialize(new VideoPayload(video.BoulderId, status.ToString(), hasHls)),
        SchemaVersion = FeedSchema.PayloadVersion,
    };

    /// <summary>A <c>hold.changed</c> row as <c>HoldFeedMapper</c> writes it.</summary>
    public static WallChange Hold(Guid wallId, Guid holdId, int generation, Guid? panelId, Guid? actor) => new()
    {
        WallId = wallId,
        Kind = FeedKinds.HoldChanged,
        EntityId = holdId,
        Audience = FeedAudience.Wall,
        ActorUserId = actor,
        DataJson = FeedJson.Serialize(new HoldPayload(generation, panelId)),
        SchemaVersion = FeedSchema.PayloadVersion,
    };

    /// <summary>
    /// <c>wall.manifest</c>: a stored photo of the wall was rewritten, so its hash and url moved. A staged photo is the
    /// editors' (<see cref="FeedAudience.Setters"/>).
    /// </summary>
    public static WallChange Manifest(Guid wallId, FeedAudience audience, Guid? actor) =>
        Plain(FeedKinds.WallManifest, wallId, wallId, audience, actor);

    /// <summary>
    /// <c>wall.updated</c> as <c>WallFeedMapper</c> writes it, for a bulk write that changed the wall's own or its panels'
    /// photo columns: circuits drop their cached aggregate, which carries the photo hashes. A staged photo is the
    /// editors' (<see cref="FeedAudience.Setters"/>).
    /// </summary>
    public static WallChange WallUpdated(Guid wallId, int generation, FeedAudience audience, Guid? actor) => new()
    {
        WallId = wallId,
        Kind = FeedKinds.WallUpdated,
        EntityId = wallId,
        Audience = audience,
        ActorUserId = actor,
        DataJson = FeedJson.Serialize(new WallPayload(generation, OwnerChanged: false)),
        SchemaVersion = FeedSchema.PayloadVersion,
    };

    /// <summary>A <c>gallery.changed</c> row as <c>GalleryFeedMapper</c> writes it.</summary>
    public static WallChange Gallery(Guid wallId, Guid imageId, Guid? actor) =>
        Plain(FeedKinds.GalleryChanged, wallId, imageId, FeedAudience.Wall, actor);

    /// <summary>A <c>view3d.changed</c> row: the wall's 3D models changed.</summary>
    public static WallChange View3D(Guid wallId, Guid modelId) =>
        Plain(FeedKinds.View3DChanged, wallId, modelId, FeedAudience.Wall, actor: null);

    /// <summary>
    /// <c>ticks.resync</c> to one user, wall-less: their external ascents changed in bulk (an import, a grade mapping, a
    /// disconnect). External ascents are theirs alone, so no wall hears of it.
    /// </summary>
    public static WallChange TicksResync(Guid userId) => new()
    {
        Kind = FeedKinds.TicksResync,
        EntityId = userId,
        Audience = FeedAudience.User,
        AudienceUserId = userId,
        ActorUserId = userId,
        SchemaVersion = FeedSchema.PayloadVersion,
    };

    /// <summary>
    /// <c>user.resync</c> to one user, wall-less: their memberships, ratings, favourites and history changed in bulk (another
    /// account was merged into theirs), so their devices reload everything of theirs.
    /// </summary>
    public static WallChange UserResync(Guid userId) => new()
    {
        Kind = FeedKinds.UserResync,
        EntityId = userId,
        Audience = FeedAudience.User,
        AudienceUserId = userId,
        ActorUserId = userId,
        SchemaVersion = FeedSchema.PayloadVersion,
    };

    private static WallChange Plain(string kind, Guid wallId, Guid entityId, FeedAudience audience, Guid? actor) => new()
    {
        WallId = wallId,
        Kind = kind,
        EntityId = entityId,
        Audience = audience,
        ActorUserId = actor,
        SchemaVersion = FeedSchema.PayloadVersion,
    };
}
