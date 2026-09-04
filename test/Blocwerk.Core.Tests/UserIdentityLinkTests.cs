using Blocwerk.Authentication.Services;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Attaching a provider identity has to be idempotent and race-safe: a double-submitted sign-in, or two
/// concurrent /walls requests both back-filling the same login, must never fault on the unique
/// (Provider, ProviderUserId) index and surface as an HTTP 500. These cover the pre-check that resolves
/// the common cases and the Postgres 23505 classifier that resolves the genuine concurrent race.
/// </summary>
public class UserIdentityLinkTests
{
    private const string Provider = "github";
    private const string Subject = "gh-subject-12345";

    [Fact]
    public async Task FirstTimeLinkAttachesTheIdentity()
    {
        using var harness = new WallTestHarness();
        var user = await SeedUserAsync(harness, "alice");

        await using var db = harness.CreateContext();
        var result = await UserIdentityLinker.EnsureLinkedAsync(db, user, Provider, Subject);

        Assert.Equal(IdentityLinkResult.Linked, result);
        Assert.Equal(1, await CountIdentitiesAsync(harness));
    }

    [Fact]
    public async Task LinkingTheSameIdentityTwiceToTheSameUserIsANoOpSuccess()
    {
        using var harness = new WallTestHarness();
        var user = await SeedUserAsync(harness, "alice");

        await using (var db = harness.CreateContext())
        {
            Assert.Equal(IdentityLinkResult.Linked, await UserIdentityLinker.EnsureLinkedAsync(db, user, Provider, Subject));
        }

        // A repeated sign-in / double-submit resolves the existing row and does NOT insert a second one.
        await using (var db = harness.CreateContext())
        {
            var second = await UserIdentityLinker.EnsureLinkedAsync(db, user, Provider, Subject);
            Assert.Equal(IdentityLinkResult.AlreadyLinkedToUser, second);
        }

        Assert.Equal(1, await CountIdentitiesAsync(harness));
    }

    [Fact]
    public async Task LinkingAnIdentityOwnedByAnotherUserIsACleanRefusalNotAnException()
    {
        using var harness = new WallTestHarness();
        var owner = await SeedUserAsync(harness, "alice");
        var intruder = await SeedUserAsync(harness, "bob");

        await using (var db = harness.CreateContext())
        {
            await UserIdentityLinker.EnsureLinkedAsync(db, owner, Provider, Subject);
        }

        await using (var db = harness.CreateContext())
        {
            var result = await UserIdentityLinker.EnsureLinkedAsync(db, intruder, Provider, Subject);
            Assert.Equal(IdentityLinkResult.LinkedToDifferentUser, result);
        }

        // The intruder never got a row, and the original owner's single row is untouched.
        await using var check = harness.CreateContext();
        var rows = await check.UserIdentities.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(owner, rows[0].UserId);
    }

    [Fact]
    public void UniqueViolationClassifierMatchesOnly23505()
    {
        var duplicate = new DbUpdateException("dup", PostgresError("23505"));
        Assert.True(PostgresErrors.IsUniqueViolation(duplicate));

        // A different SQLSTATE (here: not-null violation) must NOT be swallowed as a duplicate.
        var otherPgError = new DbUpdateException("other", PostgresError("23502"));
        Assert.False(PostgresErrors.IsUniqueViolation(otherPgError));

        // Neither may a DbUpdateException whose cause is not a Postgres error at all.
        var nonPg = new DbUpdateException("io", new InvalidOperationException("boom"));
        Assert.False(PostgresErrors.IsUniqueViolation(nonPg));
    }

    private static PostgresException PostgresError(string sqlState) =>
        new("duplicate key value violates unique constraint", "ERROR", "ERROR", sqlState);

    private static async Task<Guid> SeedUserAsync(WallTestHarness harness, string name)
    {
        var user = new User { Identifier = $"{name}__{Guid.NewGuid():N}", DisplayName = name };
        await using var db = harness.CreateContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<int> CountIdentitiesAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        return await db.UserIdentities.CountAsync();
    }
}
