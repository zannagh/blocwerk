using Blocwerk.Authentication.Services;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The step-up in front of an irreversible account action. The interesting account is the OAuth-only
/// one: it has no password and no authenticator, so before this there was nothing to re-prove and
/// the delete sat behind the session cookie alone.
/// </summary>
public class AccountReauthTests
{
    [Fact]
    public async Task AnOAuthOnlyAccountIsRefusedWithoutAFreshProviderSignIn()
    {
        using var harness = new WallTestHarness();
        var (service, store, user) = await BuildAsync(harness, withPassword: false);

        var requirements = await service.GetRequirementsAsync();
        Assert.True(requirements.RequiresProviderReauth);
        Assert.False(requirements.RequiresPassword);

        // Nothing presented, and nothing to present: this used to return true having checked nothing.
        Assert.False(await service.VerifyAsync(null, null));
        Assert.False(await service.VerifyAsync("anything", "123456"));

        // Neither a ticket that names nobody nor one issued to somebody else can be redeemed here.
        Assert.False(await service.RedeemProviderReauthAsync("not-a-real-ticket"));
        Assert.False(await service.RedeemProviderReauthAsync(store.Issue(Guid.NewGuid())));
        Assert.False(await service.VerifyAsync(null, null));
    }

    [Fact]
    public async Task AFreshProviderSignInLetsAnOAuthOnlyAccountThrough_Once()
    {
        using var harness = new WallTestHarness();
        var (service, store, user) = await BuildAsync(harness, withPassword: false);

        var ticket = store.Issue(user.Id);

        // Redeemed on arrival, which is what destroys the ticket.
        Assert.True(await service.RedeemProviderReauthAsync(ticket));
        Assert.True(await service.HasProviderReauthAsync());
        Assert.True(await service.VerifyAsync(null, null));

        // Single-use, in both halves: the step-up is spent by the action it authorised, and the
        // ticket left behind in the address bar cannot mint another one.
        Assert.False(await service.VerifyAsync(null, null));
        Assert.False(await service.HasProviderReauthAsync());
        Assert.False(await service.RedeemProviderReauthAsync(ticket));
    }

    [Fact]
    public async Task AnAccountWithAPasswordStillHasToTypeIt()
    {
        using var harness = new WallTestHarness();
        var (service, store, user) = await BuildAsync(harness, withPassword: true);

        var requirements = await service.GetRequirementsAsync();
        Assert.True(requirements.RequiresPassword);
        Assert.False(requirements.RequiresProviderReauth);

        Assert.False(await service.VerifyAsync(null, null));
        Assert.False(await service.VerifyAsync("not-my-password", null));

        // And a step-up is no substitute for the credential the account actually holds.
        Assert.True(await service.RedeemProviderReauthAsync(store.Issue(user.Id)));
        Assert.False(await service.VerifyAsync(null, null));

        Assert.True(await service.VerifyAsync("correct horse battery", null));
    }

    [Fact]
    public async Task AWrongPasswordHereCannotLockTheOwnerOutOfSigningIn()
    {
        using var harness = new WallTestHarness();
        var (service, _, user) = await BuildAsync(harness, withPassword: true);

        // A stranger at an unattended browser, spending the login lockout's whole budget and more.
        for (int i = 0; i < 8; i++)
        {
            Assert.False(await service.VerifyAsync("wrong", null));
        }

        await using var db = harness.CreateContext();
        var row = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);

        // The persisted login lockout is untouched: the account can still be signed in to.
        Assert.Equal(0, row.FailedAuthCount);
        Assert.Null(row.LockoutUntil);
    }

    /// <summary>
    /// The unattended-browser case the step-up exists for. The ticket used to be PEEKED at on arrival
    /// and left live in the address bar for fifteen minutes, so a reload, the back button, or simply
    /// pressing Delete on the page somebody walked away from re-presented a working proof. It is
    /// redeemed on arrival now, and what survives is bound to the one request/circuit that redeemed
    /// it — so nothing carried in the URL can re-arm it.
    /// </summary>
    [Fact]
    public async Task ARedeemedStepUpCannotBeReArmedFromTheUrlInAnotherCircuit()
    {
        using var harness = new WallTestHarness();
        var (first, store, user) = await BuildAsync(harness, withPassword: false);

        var ticket = store.Issue(user.Id);
        Assert.True(await first.RedeemProviderReauthAsync(ticket));

        // A reload, a second tab, or the back button: a NEW scope over the SAME singleton store,
        // handed the same ticket the URL still carries.
        var second = ServiceOver(harness, store);

        Assert.False(await second.RedeemProviderReauthAsync(ticket));
        Assert.False(await second.HasProviderReauthAsync());
        Assert.False(await second.VerifyAsync(null, null));

        // And the circuit that did redeem it still holds its own step-up — the page it is on is the
        // one allowed to finish.
        Assert.True(await first.HasProviderReauthAsync());
    }

    private static AccountReauthService ServiceOver(WallTestHarness harness, IAccountReauthTicketStore store)
    {
        return new AccountReauthService(
            harness.CurrentUser,
            harness.DbContextFactory,
            new PasswordService(),
            Substitute.For<ITotpService>(),
            store,
            new AccountReauthThrottle(),
            new BlocwerkSettings());
    }

    private static async Task<(AccountReauthService Service, IAccountReauthTicketStore Store, User User)> BuildAsync(
        WallTestHarness harness,
        bool withPassword)
    {
        await harness.SeedWallAsync();

        var passwordService = new PasswordService();
        var user = new User
        {
            Identifier = "climber__oauth-subject",
            DisplayName = "Climber",
            PasswordHash = withPassword ? passwordService.Hash("correct horse battery") : null,
            LoginUsername = withPassword ? "climber" : null,
        };

        await using (var db = harness.CreateContext())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        harness.ActingUser = user;

        var store = new AccountReauthTicketStore();

        return (ServiceOver(harness, store), store, user);
    }
}
