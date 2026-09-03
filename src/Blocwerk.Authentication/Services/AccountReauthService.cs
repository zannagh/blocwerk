using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// <see cref="IAccountReauthService"/> over the credentials on the user row plus the OAuth step-up
/// ticket store. Read-only: it verifies, and never changes anything.
/// </summary>
/// <remarks>
/// Every account faces a real challenge. An account with a password (and an authenticator) re-types
/// them; an account with neither — the OAuth-only majority — signs in with its provider again and
/// comes back holding a single-use ticket. The one case that fails closed is an account with no
/// credential AND no configured provider: it cannot step up at all, so it cannot delete, and the page
/// says to set a password first.
/// <para>
/// The ticket is redeemed ONCE, the moment the page it lands on is up, and the resulting step-up is
/// held HERE — on the scoped instance, which is to say on that one request or circuit. This is what
/// keeps the URL from being a bearer token: after the redemption the ticket in the address bar, in
/// history and in any proxy log is dead, and a reload, a second tab or another browser starts from
/// nothing and has to walk through the provider again. The step-up itself also ages out after
/// <see cref="ProviderReauthWindow"/>, so a page left open on a shared machine stops being able to
/// delete anything long before the session does.
/// </para>
/// <para>
/// Failures are capped by <see cref="AccountReauthThrottle"/>, an in-process counter, rather than by
/// the persisted login lockout: see that class for why the two must not share a counter.
/// </para>
/// </remarks>
public sealed class AccountReauthService : IAccountReauthService
{
    private readonly ICurrentUserService currentUserService;
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IPasswordService passwordService;
    private readonly ITotpService totpService;
    private readonly IAccountReauthTicketStore ticketStore;
    private readonly AccountReauthThrottle throttle;
    private readonly BlocwerkSettings settings;

    // The redeemed provider step-up, for this request/circuit only. Never serialised, never in a URL.
    private Guid providerReauthUserId;
    private DateTimeOffset? providerReauthAt;

    public AccountReauthService(
        ICurrentUserService currentUserService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IPasswordService passwordService,
        ITotpService totpService,
        IAccountReauthTicketStore ticketStore,
        AccountReauthThrottle throttle,
        BlocwerkSettings settings)
    {
        this.currentUserService = currentUserService;
        this.dbContextFactory = dbContextFactory;
        this.passwordService = passwordService;
        this.totpService = totpService;
        this.ticketStore = ticketStore;
        this.throttle = throttle;
        this.settings = settings;
    }

    /// <summary>
    /// How long a redeemed provider step-up keeps counting. Long enough to read the page and type the
    /// confirmation phrase, short enough that walking away from the browser does not leave a live
    /// deletion behind.
    /// </summary>
    public static TimeSpan ProviderReauthWindow { get; } = TimeSpan.FromMinutes(2);

    public async Task<AccountReauthRequirements> GetRequirementsAsync()
    {
        var user = await currentUserService.GetCurrentUserAsync();

        return new AccountReauthRequirements
        {
            RequiresPassword = user.HasPassword,
            RequiresTotp = user.HasTotp,
            RequiresProviderReauth = !user.HasPassword && !user.HasTotp,
            ProviderOptions = EnabledProviders(),
        };
    }

    public async Task<bool> RedeemProviderReauthAsync(string? reauthTicket)
    {
        var user = await currentUserService.GetCurrentUserAsync();

        // Spend it here and now. The caller is expected to strip the ticket from the address bar
        // straight afterwards, but even if it stays there it is already worthless.
        if (!ticketStore.Consume(reauthTicket, user.Id))
        {
            return false;
        }

        providerReauthUserId = user.Id;
        providerReauthAt = DateTimeOffset.UtcNow;
        return true;
    }

    public async Task<bool> HasProviderReauthAsync()
    {
        var user = await currentUserService.GetCurrentUserAsync();
        return HasLiveProviderReauth(user.Id);
    }

    public async Task<bool> VerifyAsync(string? password, string? totpCode)
    {
        var user = await currentUserService.GetCurrentUserAsync();

        if (throttle.IsBlocked(user.Id))
        {
            return false;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync();
        var dbUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == user.Id);
        if (dbUser is null || dbUser.IsDeleted)
        {
            return false;
        }

        // An account with no local credential is not "already proven" — it has simply moved its proof
        // to the provider, and this request/circuit must have redeemed a step-up from a sign-in that
        // just happened.
        if (!dbUser.HasPassword && !dbUser.HasTotp)
        {
            if (!HasLiveProviderReauth(dbUser.Id))
            {
                throttle.RegisterFailure(dbUser.Id);
                return false;
            }

            // Good for exactly one irreversible action, matching the single use the ticket had.
            providerReauthAt = null;
            providerReauthUserId = Guid.Empty;

            throttle.Reset(dbUser.Id);
            return true;
        }

        // Each credential the account actually holds is checked; by the branch above it holds at
        // least one, so this can never fall through having verified nothing.
        bool passwordOk = !dbUser.HasPassword || VerifyPassword(dbUser.PasswordHash, password);
        bool totpOk = !dbUser.HasTotp || VerifyTotp(dbUser.TotpEnabled, dbUser.TotpSecretProtected, totpCode);

        if (!passwordOk || !totpOk)
        {
            throttle.RegisterFailure(dbUser.Id);
            return false;
        }

        throttle.Reset(dbUser.Id);
        return true;
    }

    private bool HasLiveProviderReauth(Guid userId)
    {
        return providerReauthAt is { } at
               && providerReauthUserId == userId
               && DateTimeOffset.UtcNow - at <= ProviderReauthWindow;
    }

    private IReadOnlyList<string> EnabledProviders()
    {
        var providers = new List<string>();

        if (settings.GitHubOAuth.Enabled)
        {
            providers.Add("github");
        }

        if (settings.GoogleOAuth.Enabled)
        {
            providers.Add("google");
        }

        if (settings.MicrosoftOAuth.Enabled)
        {
            providers.Add("microsoft");
        }

        return providers;
    }

    private bool VerifyPassword(string? hash, string? password)
    {
        // Only reached for an account that HAS a password, so an empty hash here is a broken row
        // rather than "nothing to check" — refuse it.
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        return passwordService.Verify(hash, password);
    }

    private bool VerifyTotp(bool totpEnabled, string? secretProtected, string? code)
    {
        // An account without an authenticator has nothing to prove HERE; its first factor was already
        // checked above, and this method is never the only check that ran.
        if (!totpEnabled || string.IsNullOrEmpty(secretProtected))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        string secret;
        try
        {
            secret = totpService.Unprotect(secretProtected);
        }
        catch (Exception)
        {
            return false;
        }

        return totpService.Verify(secret, code);
    }
}
