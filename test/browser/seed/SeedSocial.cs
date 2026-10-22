// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Browser.Seed;

/// <summary>
/// Comments and activity on alpha, for the kiosk's read-only view: a published boulder Carol commented on and sent, a draft
/// and an archived boulder with entries of their own (the tablet must show neither), and Carol joining the wall (a
/// membership entry the tablet leaves out).
/// </summary>
internal static class SeedSocial
{
    public const string Comment = "Heel hook the arete";

    public static SeededSocial Add(BlocwerkDbContext db, Guid wallId, Guid setterId, Guid climberId)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var published = Boulder(db, wallId, setterId, "Social boulder", isDraft: false, isArchived: false);
        var draft = Boulder(db, wallId, setterId, "Secret draft", isDraft: true, isArchived: false);
        var archived = Boulder(db, wallId, setterId, "Old archived", isDraft: false, isArchived: true);

        db.BoulderComments.Add(new BoulderComment { BoulderId = published.Id, UserId = climberId, Text = Comment });
        db.BoulderComments.Add(new BoulderComment { BoulderId = archived.Id, UserId = climberId, Text = "Old beta" });
        void Log(Guid? boulderId, ActivityType type, Guid userId, int minute, string? details = null) =>
            db.ActivityLog.Add(new ActivityLogEntry
            {
                WallId = wallId, BoulderId = boulderId, UserId = userId, Type = type, Details = details, Timestamp = start.AddMinutes(minute),
            });

        Log(null, ActivityType.MemberJoined, climberId, 0);
        Log(published.Id, ActivityType.BoulderCreated, setterId, 1);
        Log(draft.Id, ActivityType.BoulderCreated, setterId, 2);
        Log(archived.Id, ActivityType.CommentAdded, climberId, 3);
        Log(published.Id, ActivityType.CommentAdded, climberId, 4);
        Log(published.Id, ActivityType.AttemptLogged, climberId, 5, "Send");
        return new SeededSocial(published.Id, draft.Id, archived.Id, Comment);
    }

    private static Boulder Boulder(BlocwerkDbContext db, Guid wallId, Guid setterId, string name, bool isDraft, bool isArchived)
    {
        var boulder = new Boulder { WallId = wallId, Name = name, CreatedByUserId = setterId, IsDraft = isDraft, IsArchived = isArchived };
        db.Boulders.Add(boulder);
        return boulder;
    }
}
