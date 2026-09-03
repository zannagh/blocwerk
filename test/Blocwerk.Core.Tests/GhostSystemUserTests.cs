using Blocwerk.Authentication.Services;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The Ghost system row is not an account, and nothing in the app may ever treat it as one: no login
/// may resolve onto it, no merge may consume or absorb it, and the content it owns must still be
/// administrable by the humans who run the wall.
/// </summary>
public class GhostSystemUserTests
{
    /// <summary>
    /// The identifier's protection is structural, not statistical. Every identifier a login can mint
    /// goes through a "{something}__{something}" shape; Ghost's contains no "__" at all, so neither
    /// the full-identifier lookup nor the legacy subject-suffix lookup can produce it.
    /// </summary>
    [Fact]
    public void TheGhostIdentifierCannotBeMintedByAnyLoginPath()
    {
        Assert.DoesNotContain("__", GhostUser.Identifier);

        // Every OAuth and dev login identifier comes from here, and it always joins with "__".
        var oauth = ClaimsHelper
            .ClaimsIdentityFromUserNameAndId(GhostUser.Identifier, GhostUser.Identifier)
            .ToUserIdentifier();
        Assert.Contains("__", oauth);
        Assert.NotEqual(GhostUser.Identifier, oauth);

        // Password signup and the deletion tombstone use the same convention.
        Assert.Contains("__", PlaceholderIdentity.DeletedIdentifier(Guid.NewGuid()));

        // And the legacy subject the resolver reads is the whole identifier, not a "ghost" suffix —
        // which is exactly what the old "system__ghost" value handed out.
        Assert.Equal(GhostUser.Identifier, GhostUser.Create().UserAuthId);
        Assert.NotEqual("ghost", GhostUser.Create().UserAuthId);
    }

    /// <summary>
    /// The second, independent defence: even a system row that DOES have the legacy
    /// "{name}__{sub}" shape is refused by the resolver. Pinned by giving the seeded row exactly the
    /// identifier it used to carry and checking a provider subject of "ghost" still resolves nowhere.
    /// </summary>
    [Fact]
    public async Task TheLegacyResolverRefusesToResolveOntoASystemRow()
    {
        using var h = new WallTestHarness();
        await using var db = h.CreateContext();

        // A real legacy row of the same shape, as the control: this one MUST resolve, so the refusal
        // below is the system-row check and not a broken lookup.
        db.Users.Add(new User { Identifier = "Ana__ana-subject", DisplayName = "Ana" });

        var ghost = await db.Users.FirstAsync(u => u.Id == GhostUser.Id);
        ghost.Identifier = "system__ghost";
        await db.SaveChangesAsync();

        Assert.NotNull(await LegacyIdentityResolver.FindByLegacyIdentifierAsync(db, "ana-subject"));
        Assert.Null(await LegacyIdentityResolver.FindByLegacyIdentifierAsync(db, "ghost"));
        Assert.Null(await LegacyIdentityResolver.FindByProviderIdentityAsync(db, "google", "ghost"));
    }

    /// <summary>
    /// A UserIdentity row pointed at the system user — the tier (a) route — is refused too, so the
    /// guard does not depend on which tier found the row.
    /// </summary>
    [Fact]
    public async Task TheProviderIdentityTierAlsoRefusesASystemRow()
    {
        using var h = new WallTestHarness();
        await using var db = h.CreateContext();

        db.UserIdentities.Add(new UserIdentity
        {
            UserId = GhostUser.Id,
            Provider = "google",
            ProviderUserId = "whatever",
        });
        await db.SaveChangesAsync();

        Assert.Null(await LegacyIdentityResolver.FindByProviderIdentityAsync(db, "google", "whatever"));
    }

    /// <summary>
    /// Ghost as merge SOURCE would re-point every anonymously-set boulder in the installation onto
    /// somebody's account and then delete the seeded row the FK depends on; as merge TARGET it would
    /// bury a real person's history under a row nobody can sign in as. Both are refused, and the row
    /// is still there afterwards.
    /// </summary>
    [Fact]
    public async Task GhostCanBeNeitherSourceNorTargetOfAMerge()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        var other = await h.AddMemberAsync("other@test", WallRole.Member);

        var merge = new AccountMergeService(h.DbContextFactory, NullLogger<AccountMergeService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => merge.MergeUsersAsync(GhostUser.Id, other.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => merge.MergeUsersAsync(other.Id, GhostUser.Id));

        await using var db = h.CreateContext();
        Assert.True(await db.Users.AnyAsync(u => u.Id == GhostUser.Id));
        Assert.True(await db.Users.AnyAsync(u => u.Id == other.Id));
    }

    /// <summary>
    /// A grade proposal on a Ghost boulder used to be unresolvable forever: accept/reject were
    /// creator-only, and nobody signs in as Ghost. A wall admin can now clear it.
    /// </summary>
    [Fact]
    public async Task AWallAdminCanResolveAGradeProposalOnAGhostBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulderId = await SeedGhostBoulderAsync(h, holds[0].Id);

        // Anyone but the creator may propose, and Ghost is nobody, so any member can.
        var member = await h.AddMemberAsync("proposer@test", WallRole.Member);
        h.ActingUser = member;
        var proposal = await h.BoulderService.ProposeGradeAsync(boulderId, "7A");

        // The wall owner administers the wall; before this they could not touch the proposal at all.
        h.ActingUser = h.Owner;
        await h.BoulderService.AcceptGradeProposalAsync(proposal.Id);

        Assert.Null(await h.BoulderService.GetActiveProposalAsync(boulderId));

        await using var db = h.CreateContext();
        Assert.Equal("7A", (await db.Boulders.FirstAsync(b => b.Id == boulderId)).Grade);
    }

    /// <summary>The reject half of the same gate.</summary>
    [Fact]
    public async Task AWallAdminCanRejectAGradeProposalOnAGhostBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulderId = await SeedGhostBoulderAsync(h, holds[0].Id);

        var member = await h.AddMemberAsync("proposer@test", WallRole.Member);
        h.ActingUser = member;
        var proposal = await h.BoulderService.ProposeGradeAsync(boulderId, "7A");

        h.ActingUser = h.Owner;
        await h.BoulderService.RejectGradeProposalAsync(proposal.Id);

        Assert.Null(await h.BoulderService.GetActiveProposalAsync(boulderId));

        await using var db = h.CreateContext();
        Assert.Null((await db.Boulders.FirstAsync(b => b.Id == boulderId)).Grade);
    }

    /// <summary>
    /// A gym resets a wall; the boulders set anonymously at the tablet go historic like every other
    /// one, and a wall admin must be able to file them away. Creator-only made that impossible.
    /// </summary>
    [Fact]
    public async Task AWallAdminCanArchiveAGhostBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulderId = await SeedGhostBoulderAsync(h, holds[0].Id, historic: true);

        h.ActingUser = h.Owner;
        await h.BoulderService.ArchiveBoulderAsync(boulderId);

        await using (var db = h.CreateContext())
        {
            Assert.True((await db.Boulders.FirstAsync(b => b.Id == boulderId)).IsArchived);
        }

        await h.BoulderService.UnarchiveBoulderAsync(boulderId);

        await using (var db = h.CreateContext())
        {
            Assert.False((await db.Boulders.FirstAsync(b => b.Id == boulderId)).IsArchived);
        }
    }

    /// <summary>
    /// Junk set at an unattended tablet has to be removable, and a Ghost boulder with no setters
    /// leaves the wall admin as the ONLY actor the rule admits — the throttle's damage ceiling
    /// assumes exactly that. Deleting used to be open to everyone; it must not now be closed to
    /// everyone.
    /// </summary>
    [Fact]
    public async Task AWallAdminCanDeleteAGhostBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulderId = await SeedGhostBoulderAsync(h, holds[0].Id);

        // An ordinary member cannot clean up after the tablet...
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        h.ActingUser = member;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.DeleteBoulderAsync(boulderId));

        // ...but the admin of the wall can, which is the whole cleanup path.
        h.ActingUser = h.Owner;
        await h.BoulderService.DeleteBoulderAsync(boulderId);

        await using var db = h.CreateContext();
        Assert.False(await db.Boulders.AnyAsync(b => b.Id == boulderId));
    }

    /// <summary>
    /// A credited setter is the other half of the widened rule: the person whose name is on the
    /// boulder can file it away even though the row's creator is the Ghost system user.
    /// </summary>
    [Fact]
    public async Task ASetterCanArchiveABoulderTheyDidNotCreate()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var setter = await h.AddMemberAsync("setter@test", WallRole.Member);
        var boulderId = await SeedGhostBoulderAsync(h, holds[0].Id, historic: true, setterId: setter.Id);

        h.ActingUser = setter;
        await h.BoulderService.ArchiveBoulderAsync(boulderId);

        await using var db = h.CreateContext();
        Assert.True((await db.Boulders.FirstAsync(b => b.Id == boulderId)).IsArchived);
    }

    /// <summary>
    /// And the rule is still a rule: an ordinary member who neither set it nor administers the wall
    /// is refused, and nothing changes.
    /// </summary>
    [Fact]
    public async Task AnUnrelatedMemberStillCannotArchive()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulderId = await SeedGhostBoulderAsync(h, holds[0].Id, historic: true);
        var stranger = await h.AddMemberAsync("stranger@test", WallRole.Member);

        h.ActingUser = stranger;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.ArchiveBoulderAsync(boulderId));

        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.FirstAsync(b => b.Id == boulderId)).IsArchived);
    }

    /// <summary>
    /// A boulder shaped exactly like one set at an unattended tablet: created by the Ghost system
    /// row, so no human being is its creator.
    /// </summary>
    private static async Task<Guid> SeedGhostBoulderAsync(
        WallTestHarness h,
        Guid holdId,
        bool historic = false,
        Guid? setterId = null)
    {
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Kiosk Line", null, [new BoulderHoldInput(holdId)]);

        await using var db = h.CreateContext();
        var stored = await db.Boulders.FirstAsync(b => b.Id == boulder.Id);
        stored.CreatedByUserId = GhostUser.Id;
        stored.IsHistoric = historic;

        if (setterId is { } id)
        {
            db.BoulderSetters.Add(new BoulderSetter { BoulderId = stored.Id, UserId = id });
        }

        await db.SaveChangesAsync();
        return stored.Id;
    }
}
