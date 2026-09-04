namespace Blocwerk.Authentication.Services;

/// <summary>
/// Outcome of an idempotent, race-safe attempt to attach a provider identity to a user.
/// </summary>
internal enum IdentityLinkResult
{
    /// <summary>The identity did not exist and was newly attached to the user.</summary>
    Linked,

    /// <summary>The identity already belonged to the SAME user — a no-op success.</summary>
    AlreadyLinkedToUser,

    /// <summary>The identity already belonged to a DIFFERENT user — a clean domain refusal, not a fault.</summary>
    LinkedToDifferentUser,
}
