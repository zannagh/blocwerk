namespace Blocwerk.Core.Enums;

/// <summary>
/// The kinds of push notification the app can send. Stored per-user as an OPT-OUT bitmask on
/// <see cref="Entities.User.DisabledNotifications"/>: a set bit means the user has turned that type
/// OFF. A zero mask (the default) therefore means every type is on, and any type added here later is
/// on-by-default for existing users without a data migration.
/// </summary>
[Flags]
public enum NotificationType
{
    /// <summary>No type. Used as the "nothing opted out" default value of the stored mask.</summary>
    None = 0,

    /// <summary>Someone started a session on a wall you are a member of.</summary>
    SessionStarted = 1,

    /// <summary>The app is back online after an update (sent only after a real maintenance window).</summary>
    AppOnline = 2,

    /// <summary>A boulder was published to a wall you are a member of.</summary>
    BoulderAdded = 4,

    /// <summary>Someone commented on a boulder you set.</summary>
    CommentOnYourBoulder = 8,

    /// <summary>Someone sent or flashed a boulder you set.</summary>
    SendOnYourBoulder = 16,

    /// <summary>Someone added a beta video to a boulder you set.</summary>
    BetaOnYourBoulder = 32,

    /// <summary>Someone joined a wall you are a member of.</summary>
    MemberJoined = 64,
}
