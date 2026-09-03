namespace Blocwerk.Core.Helpers;

/// <summary>
/// The one placeholder identity the app shows wherever a real person is not — or is no longer —
/// behind a piece of content.
/// </summary>
/// <remarks>
/// Two situations share this name on purpose, so the wall never shows two different words for
/// "we don't know who": a boulder whose setter was never recorded, and a boulder whose setter has
/// since deleted their account. Account deletion does not remove the content a person contributed
/// (that is shared gym data other people's walls depend on); it scrubs the personal columns off the
/// user row and leaves the row behind as a tombstone whose display name is
/// <see cref="DisplayName"/>. Every "set by" / "created by" / comment byline therefore keeps
/// rendering, credited to Ghost, with no call site needing to know a deletion happened.
/// </remarks>
public static class PlaceholderIdentity
{
    /// <summary>The display name shown for an unknown or deleted person.</summary>
    public const string DisplayName = "Ghost";

    /// <summary>
    /// The opaque <see cref="Entities.User.Identifier"/> a deleted account is rewritten to. The
    /// column is required and uniquely indexed, so the tombstone still needs a value; this one
    /// carries no personal data and stays unique by construction.
    /// </summary>
    public static string DeletedIdentifier(Guid userId) => $"deleted__{userId:N}";

    /// <summary>True when <paramref name="name"/> is the placeholder rather than a real person.</summary>
    public static bool IsPlaceholder(string? name) =>
        string.Equals(name?.Trim(), DisplayName, StringComparison.OrdinalIgnoreCase);
}
