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
    AttemptLogged = 40,
    CommentAdded = 50,
    BetaVideoUploaded = 60,
}
