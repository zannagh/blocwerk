namespace Blocwerk.Core.Services;

/// <summary>One wall that changes hands when its owner deletes their account.</summary>
public sealed class AccountDeletionWallTransfer
{
    public Guid WallId { get; init; }

    public required string WallName { get; init; }

    public Guid NewOwnerId { get; init; }

    /// <summary>Display name of the wall admin the wall goes to.</summary>
    public required string NewOwnerName { get; init; }
}
