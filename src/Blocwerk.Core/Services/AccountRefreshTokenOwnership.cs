namespace Blocwerk.Core.Services;

/// <summary>
/// Which <see cref="Entities.RefreshToken"/> rows belong to the account being erased.
/// </summary>
/// <remarks>
/// The table has no foreign key to <c>Users</c>: a row carries the raw OAuth subject in
/// <c>UserId</c> and the person's display name at issue time in <c>UserName</c>. Matching on the
/// subject alone is wrong in both directions — two accounts can answer to the same subject across
/// two providers (so one person's deletion would sign the other out and destroy their token), while
/// a subject the person no longer holds leaves rows behind that still carry their real name. So the
/// subjects are split: one set nobody else answers to, where every row is theirs whatever name it
/// carries, and one set that is shared, where only rows bearing one of their own names are.
/// </remarks>
public sealed class AccountRefreshTokenOwnership
{
    /// <summary>Provider subjects only this account answers to.</summary>
    public required IReadOnlyList<string> ExclusiveSubjects { get; init; }

    /// <summary>Provider subjects another account also answers to.</summary>
    public required IReadOnlyList<string> SharedSubjects { get; init; }

    /// <summary>
    /// Every name this account has been known by (OAuth name, chosen name, login username, and the
    /// name half of its legacy identifier), used to tell its rows apart on a shared subject.
    /// </summary>
    public required IReadOnlyList<string> KnownNames { get; init; }
}
