namespace Blocwerk.Core.Services;

/// <summary>
/// Erases a person from Blocwerk while leaving the shared gym content they contributed in place.
/// </summary>
/// <remarks>
/// Deletion is an ANONYMISATION, not a row delete. Boulders, setter credits, comments, ratings,
/// attempts and wall history are things other members' walls are built on; removing them would tear
/// holes in other people's data. So every personal column on the user row is scrubbed, the
/// exclusively-personal tables are emptied, and the (now content-free) user row stays behind as a
/// tombstone rendering as <see cref="Helpers.PlaceholderIdentity.DisplayName"/>.
/// </remarks>
public interface IAccountDeletionService
{
    /// <summary>
    /// What <see cref="DeleteAsync"/> would do, without doing it. Returns a preview whose
    /// <see cref="AccountDeletionPreview.CanDelete"/> is false when a solely-owned wall blocks it.
    /// </summary>
    Task<AccountDeletionPreview> PreviewAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Erases the account. Runs entirely inside one transaction, so a failure at any step leaves the
    /// account exactly as it was. Deleting an already-deleted account, or an id that does not exist,
    /// is a no-op returning false rather than an error.
    /// </summary>
    /// <exception cref="AccountDeletionBlockedException">
    /// The user solely owns a wall with no other admin to take it over.
    /// </exception>
    Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default);
}
