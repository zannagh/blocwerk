using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// The seeded system <see cref="User"/> row that owns content nobody signed in for — today, a
/// boulder set at an unattended kiosk tablet.
/// </summary>
/// <remarks>
/// <b>This is the single source of truth for the Ghost identity's ROW.</b> The NAME it renders under
/// is <see cref="PlaceholderIdentity.DisplayName"/> and is not restated here: an account that has
/// since been deleted is a different situation with the same answer, and the wall must never show
/// two different words for "we don't know who". Anything that needs the placeholder name reads it
/// from <see cref="PlaceholderIdentity"/>; anything that needs a user ID to attribute unattended
/// content to reads it from here.
/// <para>
/// A real row, rather than a nullable <c>Boulder.CreatedByUserId</c>, because that column is
/// non-nullable, uniquely indexed, restrict-deleted and read by a dozen authorisation checks
/// (archive gating, grade proposals, revise, the "mine only" filter). Making it nullable would have
/// meant auditing and loosening every one of them; a row that simply is not anybody keeps all of
/// them true as written — Ghost is nobody's <c>_currentUserId</c>, so every "is this mine?" check
/// answers false, which is exactly right.
/// </para>
/// <para>
/// Seeded through the model (<c>HasData</c>), so it arrives with the migration in production and
/// with <c>EnsureCreated</c> in tests, and can never be missing when the FK needs it.
/// </para>
/// </remarks>
public static class GhostUser
{
    /// <summary>
    /// The reserved user ID. A fixed, obviously-synthetic value: it has to be stable across
    /// databases so the seed row is idempotent, and it must never collide with a generated one.
    /// </summary>
    public static readonly Guid Id = new("00000000-0000-4000-8000-000000000001");

    /// <summary>
    /// The row's <see cref="User.Identifier"/>. The column is required and uniquely indexed, so the
    /// system row needs one.
    /// </summary>
    /// <remarks>
    /// <b>The absence of "__" is the whole protection, and it is structural rather than a claim
    /// about how unlikely a collision is.</b> Every identifier a login can mint contains a double
    /// underscore: OAuth and dev logins go through <c>ClaimsHelper.ToUserIdentifier()</c>, which
    /// returns <c>"{name}__{id}"</c> and returns empty rather than a bare name when either half is
    /// missing; password signup mints <c>"local__{guid:N}"</c>; a deleted account's tombstone is
    /// <see cref="PlaceholderIdentity.DeletedIdentifier"/>, i.e. <c>"deleted__{guid:N}"</c>. So:
    /// <list type="bullet">
    /// <item><description>the full-identifier lookup cannot match, because no minted identifier can
    /// equal a string with no "__" in it;</description></item>
    /// <item><description>the legacy subject lookup cannot match either, because it searches for
    /// <c>Identifier.EndsWith("__" + subject)</c> and reads the subject as
    /// <see cref="User.UserAuthId"/> — the segment after the LAST "__". A string containing none can
    /// never satisfy that suffix.</description></item>
    /// </list>
    /// The earlier value ("system__ghost") had exactly the legacy <c>{name}__{sub}</c> shape, so its
    /// <see cref="User.UserAuthId"/> was "ghost" and a provider subject of "ghost" resolved straight
    /// onto this row. Belt and braces: <c>LegacyIdentityResolver</c> now also refuses to return a
    /// system row from either tier, and <c>AccountMergeService</c> refuses Ghost as merge source or
    /// target — so this constant is the first of three independent defences, not the only one.
    /// </remarks>
    public const string Identifier = "system:ghost";

    /// <summary>
    /// A fixed creation timestamp. <c>HasData</c> is compared against the model on every scaffold,
    /// so <c>DateTimeOffset.UtcNow</c> here would make EF believe the seed changed and emit a
    /// pointless migration every time.
    /// </summary>
    private static readonly DateTimeOffset SeededAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>True when this ID is the Ghost row rather than a real person.</summary>
    public static bool Is(Guid userId)
    {
        return userId == Id;
    }

    /// <summary>
    /// True when this identifier belongs to a SYSTEM row rather than to any person's account. Used
    /// by identity resolution to refuse to hand a login a system row, whatever route it arrived by.
    /// </summary>
    public static bool IsSystemIdentifier(string? identifier)
    {
        return string.Equals(identifier, Identifier, StringComparison.Ordinal);
    }

    /// <summary>The seed row, as the model seeds it and as tests can assert against.</summary>
    public static User Create()
    {
        return new User
        {
            Id = Id,
            Identifier = Identifier,
            DisplayName = PlaceholderIdentity.DisplayName,
            CreatedAt = SeededAt,
        };
    }
}
