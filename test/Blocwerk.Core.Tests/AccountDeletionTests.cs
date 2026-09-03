using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Account deletion: personal data is really gone from the columns, the shared gym content the
/// person contributed really survives under the placeholder, and a wall never ends up ownerless.
/// </summary>
/// <remarks>
/// Every assertion here reads the persisted columns back rather than trusting a return value —
/// a test that only checked "no exception" would still pass if deletion did nothing at all.
/// </remarks>
public class AccountDeletionTests
{
    [Fact]
    public async Task Delete_ScrubsEveryPersonalColumnOffTheUserRow()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);

        Assert.True(await fixture.Service.DeleteAsync(fixture.LeaverId));

        await using var db = harness.CreateContext();
        var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == fixture.LeaverId);

        Assert.Null(row.Email);
        Assert.False(row.EmailVerified);
        Assert.Null(row.CustomDisplayName);
        Assert.Null(row.AvatarImage);
        Assert.Null(row.AvatarContentType);
        Assert.Null(row.LoginUsername);
        Assert.Null(row.PasswordHash);
        Assert.Null(row.TotpSecretProtected);
        Assert.False(row.TotpEnabled);
        Assert.Null(row.TotpLastUsedStep);
        Assert.Equal(0, row.FailedAuthCount);
        Assert.Null(row.LockoutUntil);
        Assert.Null(row.HomeWallId);

        // The identifier is the OAuth subject; it must not survive either.
        Assert.DoesNotContain("leaver", row.Identifier, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlaceholderIdentity.DeletedIdentifier(fixture.LeaverId), row.Identifier);

        // An elevated role never outlives the person who held it.
        Assert.Equal(IdentityRole.User, row.Role);

        // What is left renders as the placeholder, and is stamped as deleted.
        Assert.Equal(PlaceholderIdentity.DisplayName, row.DisplayName);
        Assert.Equal(PlaceholderIdentity.DisplayName, row.Name);
        Assert.True(row.IsDeleted);
        Assert.NotNull(row.DeletedAt);
    }

    [Fact]
    public async Task Delete_RemovesCredentialsLinksAndPrivateHistory()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);
        var leaver = fixture.LeaverId;

        await fixture.Service.DeleteAsync(leaver);

        await using var db = harness.CreateContext();

        Assert.Empty(await db.UserIdentities.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.RefreshTokens.Where(x => x.UserId == DeletionFixture.LeaverSubject).ToListAsync());
        Assert.Empty(await db.EmailVerificationCodes.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.ApiKeys.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.TopLoggerConnections.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.ExternalAscents.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.UserGradeMappings.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.BoulderFavorites.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.HangboardSessions.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.PullupSessions.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.ClimbingSessions.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.Activities.Where(x => x.UserId == leaver).ToListAsync());
        Assert.Empty(await db.BetaVideos.Where(x => x.UploadedByUserId == leaver).ToListAsync());

        // The membership goes, and with it the kiosk PIN hash and the kiosk consent stamp.
        Assert.Empty(await db.WallMembers.Where(x => x.UserId == leaver).ToListAsync());

        // The clip's file on disk is unlinked too, not just its row.
        fixture.BetaVideoStorage.Received().Delete("leaver-clip.mp4");
    }

    [Fact]
    public async Task Delete_KeepsAuthoredContentAndRendersItAsThePlaceholder()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);
        var leaver = fixture.LeaverId;

        await fixture.Service.DeleteAsync(leaver);

        await using var db = harness.CreateContext();

        var boulder = await db.Boulders
            .AsNoTracking()
            .Include(b => b.CreatedBy)
            .SingleAsync(b => b.Id == fixture.BoulderId);

        Assert.Equal("Leaver's problem", boulder.Name);
        Assert.Equal(leaver, boulder.CreatedByUserId);
        Assert.Equal(PlaceholderIdentity.DisplayName, boulder.CreatedBy.Name);

        Assert.Single(await db.BoulderSetters.Where(s => s.UserId == leaver).ToListAsync());
        Assert.Single(await db.BoulderComments.Where(c => c.UserId == leaver).ToListAsync());
        Assert.Single(await db.BoulderRatings.Where(r => r.UserId == leaver).ToListAsync());
        Assert.Single(await db.GradeProposals.Where(p => p.ProposedByUserId == leaver).ToListAsync());
        Assert.Single(await db.WallResets.Where(r => r.ResetByUserId == leaver).ToListAsync());
        Assert.Single(await db.ActivityLog.Where(a => a.UserId == leaver).ToListAsync());

        // The attempt survives so the boulder's send count does not silently change under the
        // other members, even though its private activity cluster was deleted.
        var attempt = await db.Attempts.AsNoTracking().SingleAsync(a => a.UserId == leaver);
        Assert.Equal(AttemptType.Send, attempt.Type);
        Assert.Null(attempt.ActivityId);

        // And the setter byline renders the placeholder rather than a blank.
        var names = await BoulderSetterNames.LoadForWallAsync(db, harness.WallId);
        Assert.Equal(PlaceholderIdentity.DisplayName, BoulderSetterNames.Format(names[fixture.BoulderId]));
    }

    [Fact]
    public async Task Delete_TransfersASolelyOwnedWallToItsLongestStandingAdmin()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);
        harness.ActingUser = harness.Owner;

        var earlyAdmin = await harness.AddMemberAsync("early-admin", WallRole.Admin);
        var lateAdmin = await harness.AddMemberAsync("late-admin", WallRole.Admin);

        await using (var setup = harness.CreateContext())
        {
            var early = await setup.WallMembers.SingleAsync(m => m.UserId == earlyAdmin.Id);
            early.JoinedAt = DateTimeOffset.UtcNow.AddYears(-2);
            var late = await setup.WallMembers.SingleAsync(m => m.UserId == lateAdmin.Id);
            late.JoinedAt = DateTimeOffset.UtcNow;
            await setup.SaveChangesAsync();
        }

        var preview = await fixture.Service.PreviewAsync(harness.Owner.Id);
        Assert.True(preview.CanDelete);
        var transfer = Assert.Single(preview.WallTransfers);
        Assert.Equal(earlyAdmin.Id, transfer.NewOwnerId);

        Assert.True(await fixture.Service.DeleteAsync(harness.Owner.Id));

        await using var db = harness.CreateContext();
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == harness.WallId);
        Assert.Equal(earlyAdmin.Id, wall.OwnerId);

        // The wall's own sensor/kiosk key follows the wall rather than dying with the admin who
        // minted it; the departing user's personal key is gone.
        var keys = await db.ApiKeys.AsNoTracking().ToListAsync();
        Assert.Contains(keys, k => k.Scope == ApiKeyScope.Wall && k.UserId == earlyAdmin.Id);
        Assert.DoesNotContain(keys, k => k.UserId == harness.Owner.Id);
    }

    [Fact]
    public async Task Delete_IsRefusedWhenASolelyOwnedWallHasNoOtherAdmin()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);
        harness.ActingUser = harness.Owner;

        // A plain member is deliberately NOT a successor: handing them the wall would grant
        // wall-admin authority nobody gave them.
        await harness.AddMemberAsync("just-a-member", WallRole.Member);

        var preview = await fixture.Service.PreviewAsync(harness.Owner.Id);
        Assert.False(preview.CanDelete);
        Assert.Contains("Test Wall", preview.BlockingWallNames);

        var blocked = await Assert.ThrowsAsync<AccountDeletionBlockedException>(
            () => fixture.Service.DeleteAsync(harness.Owner.Id));
        Assert.Contains("Test Wall", blocked.WallNames);

        // Nothing was touched: the owner is still a live account owning a live wall.
        await using var db = harness.CreateContext();
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Id == harness.Owner.Id);
        Assert.False(owner.IsDeleted);
        Assert.Equal("Owner", owner.DisplayName);

        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == harness.WallId);
        Assert.Equal(harness.Owner.Id, wall.OwnerId);
    }

    [Fact]
    public async Task Delete_IsIdempotentAndSurvivesAnUnknownUser()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);

        Assert.True(await fixture.Service.DeleteAsync(fixture.LeaverId));

        await using (var db = harness.CreateContext())
        {
            var first = await db.Users.AsNoTracking().SingleAsync(u => u.Id == fixture.LeaverId);
            Assert.NotNull(first.DeletedAt);

            // A second run must not throw, and must not re-stamp the audit timestamp.
            Assert.False(await fixture.Service.DeleteAsync(fixture.LeaverId));

            var second = await db.Users.AsNoTracking().SingleAsync(u => u.Id == fixture.LeaverId);
            Assert.Equal(first.DeletedAt, second.DeletedAt);
        }

        // An id that is not the signed-in user's is refused outright, existing or not — the service
        // is self-service only.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.DeleteAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.PreviewAsync(Guid.NewGuid()));

        // And a preview of the caller's own, now-erased account is an empty, unblocked preview
        // rather than a throw.
        var preview = await fixture.Service.PreviewAsync(fixture.LeaverId);
        Assert.True(preview.CanDelete);
        Assert.Equal(0, preview.BouldersKept);
        Assert.Empty(preview.WallTransfers);
    }

    [Fact]
    public async Task Delete_RollsBackEverythingWhenTheFinalScrubFails()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);

        // Park a decoy user on the exact identifier the tombstone will be rewritten to. The unique
        // index on Users.Identifier then makes the LAST step of the deletion fail — after every
        // personal row has already been deleted inside the transaction. If the transaction were not
        // real, those deletes would stand.
        await using (var setup = harness.CreateContext())
        {
            setup.Users.Add(new User
            {
                Identifier = PlaceholderIdentity.DeletedIdentifier(fixture.LeaverId),
                DisplayName = "Decoy",
            });
            await setup.SaveChangesAsync();
        }

        // Specifically the unique-index violation on the final scrub, not some earlier refusal —
        // otherwise this test would pass without the erase steps ever having run.
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.DeleteAsync(fixture.LeaverId));

        await using var db = harness.CreateContext();
        var leaver = fixture.LeaverId;

        var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == leaver);
        Assert.Null(row.DeletedAt);
        Assert.Equal(DeletionFixture.LeaverEmail, row.Email);

        Assert.NotEmpty(await db.UserIdentities.Where(x => x.UserId == leaver).ToListAsync());
        Assert.NotEmpty(await db.ApiKeys.Where(x => x.UserId == leaver).ToListAsync());
        Assert.NotEmpty(await db.WallMembers.Where(x => x.UserId == leaver).ToListAsync());
        Assert.NotEmpty(await db.HangboardSessions.Where(x => x.UserId == leaver).ToListAsync());
        Assert.NotEmpty(await db.BetaVideos.Where(x => x.UploadedByUserId == leaver).ToListAsync());

        // Nothing was unlinked from disk either, because the file sweep only runs after the commit.
        fixture.BetaVideoStorage.DidNotReceive().Delete(Arg.Any<string?>());
    }

    [Fact]
    public async Task Preview_ReportsWhatSurvivesAndWhatGoes()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);

        var preview = await fixture.Service.PreviewAsync(fixture.LeaverId);

        Assert.True(preview.CanDelete);
        Assert.Empty(preview.BlockingWallNames);
        Assert.Equal(1, preview.BouldersKept);
        Assert.Equal(1, preview.CommentsKept);
        Assert.Equal(1, preview.AttemptsKept);
        Assert.Equal(1, preview.MembershipsRemoved);
        Assert.Equal(3, preview.TrainingSessionsRemoved);
        Assert.Equal(1, preview.BetaVideosRemoved);
    }

    [Fact]
    public async Task Delete_ScrubsTheFreeTextThePersonTypedOnRowsThatSurvive()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);
        var leaver = fixture.LeaverId;

        await fixture.Service.DeleteAsync(leaver);

        await using var db = harness.CreateContext();

        // The attempt row stays (the boulder's send count must not move) but the sentence about the
        // person's own shoulder does not.
        var attempt = await db.Attempts.AsNoTracking().SingleAsync(a => a.UserId == leaver);
        Assert.Null(attempt.Notes);

        // The activity log keeps the event; the boulder name the person typed into it goes.
        var logged = await db.ActivityLog.AsNoTracking().SingleAsync(a => a.UserId == leaver);
        Assert.Equal(ActivityType.BoulderCreated, logged.Type);
        Assert.Null(logged.Details);
    }

    [Fact]
    public async Task Delete_TransfersWallKeysUnderANeutralNameRatherThanTheLeaversOwn()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);
        harness.ActingUser = harness.Owner;

        var admin = await harness.AddMemberAsync("successor-admin", WallRole.Admin);

        Assert.True(await fixture.Service.DeleteAsync(harness.Owner.Id));

        await using var db = harness.CreateContext();
        var key = await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Scope == ApiKeyScope.Wall);

        Assert.Equal(admin.Id, key.UserId);
        Assert.DoesNotContain("sensor", key.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(key.Prefix, key.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_LeavesOtherPeoplesSignupCodesAndRefreshTokensAlone()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);

        await fixture.Service.DeleteAsync(fixture.LeaverId);

        await using var db = harness.CreateContext();

        // A pending SIGNUP code for the same address belongs to no account yet — very possibly to
        // somebody else entirely — so it is none of this deletion's business.
        var codes = await db.EmailVerificationCodes.AsNoTracking().ToListAsync();
        var survivor = Assert.Single(codes);
        Assert.Equal(EmailVerificationPurpose.Signup, survivor.Purpose);
        Assert.Null(survivor.UserId);

        var tokens = await db.RefreshTokens.AsNoTracking().ToListAsync();

        // The stranger's token rides on a subject the leaver ALSO answered to (a different provider
        // with a colliding subject); deleting by subject alone would have signed them out too.
        var strangerToken = Assert.Single(tokens);
        Assert.Equal("stranger-refresh-token", strangerToken.Token);

        // ...while the leaver's own token on that shared subject, and the one carrying a name they
        // had since changed away from, are both gone.
        Assert.DoesNotContain(tokens, t => t.UserName.StartsWith("Leaver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_CommitsEvenWhenTheClipFilesCannotBeRemoved()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);

        var brokenStorage = Substitute.For<IBetaVideoStorage>();
        brokenStorage
            .When(s => s.Delete(Arg.Any<string?>()))
            .Do(_ => throw new IOException("the disk is gone"));
        var service = DeletionFixture.CreateService(harness, brokenStorage);

        // The commit is the point of no return. A storage failure after it must not be reported as a
        // failed deletion — that used to leave the person signed in on their own tombstone.
        Assert.True(await service.DeleteAsync(fixture.LeaverId));

        await using var db = harness.CreateContext();
        var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == fixture.LeaverId);
        Assert.True(row.IsDeleted);
        Assert.Empty(await db.BetaVideos.Where(v => v.UploadedByUserId == fixture.LeaverId).ToListAsync());
    }

    [Fact]
    public async Task Delete_RefusesWhenTheSuccessorDisappearsBeforeTheTransaction()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);
        harness.ActingUser = harness.Owner;

        var successor = await harness.AddMemberAsync("co-admin", WallRole.Admin);

        // Exactly the concurrent-deletion race: the wall's only other admin deletes their own account
        // in the window between the ownership decision and the transaction that applies it. If the
        // decision is not re-taken inside the transaction, the wall lands on a tombstone.
        var service = new HookedDeletionService(
            harness,
            async () =>
            {
                await using var other = harness.CreateContext();
                var row = await other.Users.SingleAsync(u => u.Id == successor.Id);
                row.DeletedAt = DateTimeOffset.UtcNow;
                await other.SaveChangesAsync();
            });

        await Assert.ThrowsAsync<AccountDeletionBlockedException>(
            () => service.DeleteAsync(harness.Owner.Id));

        await using var db = harness.CreateContext();
        var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == harness.WallId);
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Id == wall.OwnerId);

        Assert.False(owner.IsDeleted);
        Assert.Equal(harness.Owner.Id, wall.OwnerId);
    }

    [Fact]
    public async Task DeleteAndPreview_RefuseAnybodyButTheAccountsOwnUser()
    {
        using var harness = new WallTestHarness();
        var fixture = await DeletionFixture.CreateAsync(harness);

        // Signed in as the leaver, reaching for the wall owner's account.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.PreviewAsync(harness.Owner.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.DeleteAsync(harness.Owner.Id));

        await using var db = harness.CreateContext();
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Id == harness.Owner.Id);
        Assert.False(owner.IsDeleted);
    }

    /// <summary>
    /// The deletion service with the ownership seam filled in, so a test can commit a competing
    /// change in the exact window the check-then-act race lives in.
    /// </summary>
    private sealed class HookedDeletionService : AccountDeletionService
    {
        private readonly Func<Task> onOwnershipResolved;

        public HookedDeletionService(WallTestHarness harness, Func<Task> onOwnershipResolved)
            : base(
                harness.DbContextFactory,
                Substitute.For<IBetaVideoStorage>(),
                harness.CurrentUser,
                NullLogger<AccountDeletionService>.Instance)
        {
            this.onOwnershipResolved = onOwnershipResolved;
        }

        protected override Task OnOwnershipResolvedAsync(CancellationToken ct) => onOwnershipResolved();
    }
}
