// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>The wall activity card's icon and verb for each kind of entry.</summary>
public static class WallActivityText
{
    /// <summary>
    /// The word on the row's chip: what the entry is about, in the same chip style as the boulder history's
    /// Send / Flash / Attempt. A single letter said nothing on its own.
    /// </summary>
    public static string Chip(ActivityType type) => type switch
    {
        ActivityType.HoldAdded or ActivityType.HoldMoved or ActivityType.HoldDeleted
            or ActivityType.HoldColorChanged or ActivityType.HoldShapeChanged or ActivityType.HoldNamed
            or ActivityType.HoldMarkedModified or ActivityType.HoldMerged => "Hold",
        ActivityType.BoulderCreated => "New",
        ActivityType.BoulderRevised => "Revised",
        ActivityType.WallPhotoUploaded or ActivityType.WallPhotoStaged
            or ActivityType.WallPhotoConfirmed or ActivityType.WallPhotoDiscarded => "Photo",
        ActivityType.WallRecreated or ActivityType.WallReset => "Wall",
        ActivityType.MemberJoined or ActivityType.MemberRoleChanged
            or ActivityType.MemberRemoved or ActivityType.MemberLeft => "Member",
        ActivityType.AttemptLogged => "Attempt",
        ActivityType.CommentAdded => "Comment",
        ActivityType.BetaVideoUploaded => "Video",
        _ => "Other",
    };

    /// <summary>The chip's colour class: attempts as in the boulder history, boulders and members tinted, the rest neutral.</summary>
    public static string ChipTone(ActivityType type) => type switch
    {
        ActivityType.AttemptLogged => "attempt",
        ActivityType.BoulderCreated or ActivityType.BoulderRevised => "boulder",
        ActivityType.MemberJoined or ActivityType.MemberRoleChanged
            or ActivityType.MemberRemoved or ActivityType.MemberLeft => "member",
        _ => "neutral",
    };

    /// <summary>
    /// The verb with the member an entry is about, where it names one: "removed Max" rather than "removed a member".
    /// <paramref name="subjectName"/> is that member's byline, or null when the entry has none.
    /// </summary>
    public static string Verb(ActivityType type, string? subjectName) =>
        type == ActivityType.MemberRemoved && !string.IsNullOrEmpty(subjectName) ? $"removed {subjectName}" : Verb(type);

    public static string Verb(ActivityType type) => type switch
    {
        ActivityType.HoldAdded => "added a hold",
        ActivityType.HoldMoved => "moved a hold",
        ActivityType.HoldDeleted => "removed a hold",
        ActivityType.HoldColorChanged => "changed a hold color",
        ActivityType.HoldShapeChanged => "refined a hold shape",
        ActivityType.HoldNamed => "named a hold",
        ActivityType.BoulderCreated => "created a boulder",
        ActivityType.BoulderRevised => "revised a boulder",
        ActivityType.WallPhotoUploaded => "uploaded a photo",
        ActivityType.WallPhotoStaged => "staged a new photo",
        ActivityType.WallPhotoConfirmed => "confirmed the new photo",
        ActivityType.WallPhotoDiscarded => "discarded the staged photo",
        ActivityType.WallRecreated => "recreated the wall",
        ActivityType.HoldMarkedModified => "marked a hold as modified",
        ActivityType.HoldMerged => "merged holds across photos",
        ActivityType.WallReset => "reset the wall",
        ActivityType.MemberJoined => "joined the wall",
        ActivityType.MemberRoleChanged => "role was changed",
        ActivityType.MemberRemoved => "removed a member",
        ActivityType.MemberLeft => "left the wall",
        ActivityType.AttemptLogged => "logged an attempt",
        ActivityType.CommentAdded => "commented on a boulder",
        ActivityType.BetaVideoUploaded => "added a beta video",
        _ => "did something",
    };
}
