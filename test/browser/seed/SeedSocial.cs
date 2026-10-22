// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// The social wall (Erin's), for the kiosk's read-only view: Cora is a member who consented to the kiosk, Dave one who did
/// not. A published boulder Erin set, which Cora commented on and sent and Dave commented on and flashed; a second published boulder the live test comments on, so the first keeps its
/// five entries (one activity page); a draft and an archived boulder with entries of their own (the tablet shows
/// neither); and Cora joining the wall, a wall-wide entry.
/// </summary>
internal static class SeedSocial
{
    public const string Comment = "Heel hook the arete";

    public const string UnconsentedComment = "Crimp the sloper";

    public static SeededSocial Add(BlocwerkDbContext db, Guid wallId, string kioskToken, Guid setterId, Guid climberId, Guid unconsentedId)
    {
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = climberId, Role = WallRole.Member, KioskConsentedAt = DateTimeOffset.UtcNow });
        db.WallMembers.Add(new WallMember { WallId = wallId, UserId = unconsentedId, Role = WallRole.Member });

        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var published = Boulder(db, wallId, setterId, "Social boulder", isDraft: false, isArchived: false);
        var live = Boulder(db, wallId, setterId, "Live boulder", isDraft: false, isArchived: false);
        var draft = Boulder(db, wallId, setterId, "Secret draft", isDraft: true, isArchived: false);
        var archived = Boulder(db, wallId, setterId, "Old archived", isDraft: false, isArchived: true);

        db.BoulderComments.Add(new BoulderComment { BoulderId = published.Id, UserId = climberId, Text = Comment });
        db.BoulderComments.Add(new BoulderComment { BoulderId = published.Id, UserId = unconsentedId, Text = UnconsentedComment });
        db.BoulderComments.Add(new BoulderComment { BoulderId = archived.Id, UserId = climberId, Text = "Old beta" });
        void Log(Guid? boulderId, ActivityType type, Guid userId, int minute, Guid? attemptId = null, string? details = null) =>
            db.ActivityLog.Add(new ActivityLogEntry
            {
                WallId = wallId, BoulderId = boulderId, UserId = userId, Type = type, Details = details, AttemptId = attemptId,
                Timestamp = start.AddMinutes(minute),
            });

        // An attempt's entry carries its attempt, as every entry written since does; the startup cleanup deletes one without.
        Guid Ascent(Guid userId, AttemptType type, int minute)
        {
            var attempt = new Attempt { BoulderId = published.Id, UserId = userId, Type = type, Timestamp = start.AddMinutes(minute) };
            db.Attempts.Add(attempt);
            return attempt.Id;
        }

        Log(null, ActivityType.MemberJoined, climberId, 0);
        Log(published.Id, ActivityType.BoulderCreated, setterId, 1);
        Log(draft.Id, ActivityType.BoulderCreated, setterId, 2);
        Log(archived.Id, ActivityType.CommentAdded, climberId, 3);
        Log(published.Id, ActivityType.CommentAdded, climberId, 4);
        Log(published.Id, ActivityType.AttemptLogged, climberId, 5, Ascent(climberId, AttemptType.Send, 5), "Send");
        Log(published.Id, ActivityType.CommentAdded, unconsentedId, 6);
        Log(published.Id, ActivityType.AttemptLogged, unconsentedId, 7, Ascent(unconsentedId, AttemptType.Flash, 7), "Flash");
        Log(live.Id, ActivityType.BoulderCreated, setterId, 8);
        return new SeededSocial(wallId, kioskToken, published.Id, live.Id, draft.Id, archived.Id, Comment, UnconsentedComment);
    }

    private static Boulder Boulder(BlocwerkDbContext db, Guid wallId, Guid setterId, string name, bool isDraft, bool isArchived)
    {
        var boulder = new Boulder { WallId = wallId, Name = name, CreatedByUserId = setterId, IsDraft = isDraft, IsArchived = isArchived };
        db.Boulders.Add(boulder);
        return boulder;
    }
}

internal sealed record SeededSocial(
    Guid WallId, string KioskToken, Guid Boulder, Guid Live, Guid Draft, Guid Archived, string Comment, string UnconsentedComment);
