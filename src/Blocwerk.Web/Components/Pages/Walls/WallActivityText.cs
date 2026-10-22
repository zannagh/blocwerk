// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>The wall activity card's icon and verb for each kind of entry.</summary>
public static class WallActivityText
{
    public static string Icon(ActivityType type) => type switch
    {
        ActivityType.HoldAdded => "+",
        ActivityType.HoldMoved => "~",
        ActivityType.HoldDeleted => "-",
        ActivityType.HoldColorChanged => "~",
        ActivityType.HoldShapeChanged => "~",
        ActivityType.HoldNamed => "T",
        ActivityType.BoulderCreated => "B",
        ActivityType.BoulderRevised => "R",
        ActivityType.WallPhotoUploaded => "P",
        ActivityType.WallPhotoStaged => "P",
        ActivityType.WallPhotoConfirmed => "P",
        ActivityType.WallPhotoDiscarded => "P",
        ActivityType.WallRecreated => "!",
        ActivityType.HoldMarkedModified => "!",
        ActivityType.HoldMerged => "M",
        ActivityType.WallReset => "!",
        ActivityType.MemberJoined => "J",
        ActivityType.MemberRoleChanged => "R",
        ActivityType.AttemptLogged => "A",
        ActivityType.CommentAdded => "C",
        ActivityType.BetaVideoUploaded => "V",
        _ => "?",
    };

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
        ActivityType.AttemptLogged => "logged an attempt",
        ActivityType.CommentAdded => "commented on a boulder",
        ActivityType.BetaVideoUploaded => "added a beta video",
        _ => "did something",
    };
}
