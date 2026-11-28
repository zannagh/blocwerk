namespace Blocwerk.Core.Enums;

public enum ActivityType
{
    HoldAdded = 0,
    HoldMoved = 1,
    HoldDeleted = 2,
    HoldColorChanged = 3,
    HoldShapeChanged = 4,
    HoldNamed = 5,
    BoulderCreated = 10,
    BoulderArchived = 11,
    BoulderRevised = 12,
    BoulderPublished = 13,
    WallReset = 20,
    WallPhotoUploaded = 21,
    WallPhotoStaged = 22,
    WallPhotoConfirmed = 23,
    WallPhotoDiscarded = 24,
    WallRecreated = 25,
    HoldMarkedModified = 6,
    HoldMerged = 7,
    MemberJoined = 30,
    MemberRoleChanged = 31,

    /// <summary>An admin removed a member; the entry's author is the admin, its subject the person removed.</summary>
    MemberRemoved = 32,

    /// <summary>A member left the wall; the entry's author is the person who left.</summary>
    MemberLeft = 33,
    AttemptLogged = 40,
    CommentAdded = 50,
    BetaVideoUploaded = 60,
}
