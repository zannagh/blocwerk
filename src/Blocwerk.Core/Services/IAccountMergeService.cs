namespace Blocwerk.Core.Services;

/// <summary>
/// Absorbs one local user (the source) into another (the target), re-pointing every user-referencing
/// row to the target and then deleting the source. Used by OAuth account linking: when a user links a
/// provider identity that already belongs to a second account, the two accounts are merged into the
/// one the user is currently signed in as.
/// </summary>
public interface IAccountMergeService
{
    /// <summary>
    /// Merges <paramref name="sourceUserId"/> into <paramref name="targetUserId"/> in a single
    /// transaction: re-points every FK, dedups composite-PK membership/rating/favorite rows, moves the
    /// source's provider identities onto the target, drops the source's refresh tokens, keeps the
    /// higher of the two roles, and finally deletes the source user. The caller MUST already have
    /// verified that <paramref name="targetUserId"/> is the signed-in user.
    /// </summary>
    /// <param name="sourceUserId">The user being absorbed and deleted.</param>
    /// <param name="targetUserId">The surviving user that receives all of the source's data.</param>
    Task MergeUsersAsync(Guid sourceUserId, Guid targetUserId);
}
