using System.Security.Claims;
using Blocwerk.Authentication;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Kiosk;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The escalations a kiosk session must not be able to perform, and the ordinary work on its own
/// wall that must keep working regardless.
/// </summary>
/// <remarks>
/// A kiosk session deliberately carries the picked user's FULL authority over the tablet's wall,
/// destructive wall-admin actions included. That makes "the acting user is allowed to do this" the
/// wrong question everywhere below: what these tests pin down is the second axis — whether the
/// effect ESCAPES the tablet and the thirty-minute window, or reaches a wall the tablet is not
/// registered to.
/// <para>
/// Every kiosk case is wired the way production is: a real <see cref="KioskScopedDbContextFactory"/>
/// over the harness's SQLite connection, so the services under test receive contexts stamped with
/// <see cref="BlocwerkDbContext.KioskWallId"/> exactly as they would behind the middleware.
/// </para>
/// </remarks>
public class KioskEscalationTests
{
    // ---------------------------------------------------------------------------------------
    // 1. Share tokens: the escalation that outlives the session, the PIN and key revocation.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ShareToken_IsRefusedFromAKiosk_EvenActingAsAnAdminOfThatVeryWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // The acting user owns and administers this wall, and the kiosk is registered to it. Every
        // authority check passes; the refusal is purely "not from this device".
        var walls = KioskWallService(h, h.WallId);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => walls.GenerateShareTokenAsync(h.WallId));

        await using var db = h.CreateContext();
        Assert.Null(await db.Walls.IgnoreQueryFilters().Where(w => w.Id == h.WallId).Select(w => w.ShareToken).FirstAsync());
    }

    [Fact]
    public async Task ShareToken_StillWorksForAnOrdinaryAdminSession()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var token = await h.WallService.GenerateShareTokenAsync(h.WallId);
        Assert.False(string.IsNullOrWhiteSpace(token));
    }

    [Fact]
    public async Task JoiningAWall_IsRefusedFromAKiosk()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var token = await h.WallService.GenerateShareTokenAsync(h.WallId);

        // The redeeming half. Without this, "generate a link on the tablet, redeem it from a phone"
        // buys permanent membership; with the minting refused as well, neither half is reachable.
        var walls = KioskWallService(h, h.WallId);
        await Assert.ThrowsAsync<KioskRestrictedException>(() => walls.JoinWallAsync(token));
    }

    [Fact]
    public async Task CreatingAWall_IsRefusedFromAKiosk()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var walls = KioskWallService(h, h.WallId);
        await Assert.ThrowsAsync<KioskRestrictedException>(() => walls.CreateWallAsync("Tablet Wall", null));
    }

    // ---------------------------------------------------------------------------------------
    // 2. Authority over the acting user's OTHER walls.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task WallAdminGuard_RefusesAWallOtherThanTheKiosksOwn()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWallId = await SeedSecondWallAsync(h, h.Owner.Id);

        // The owner administers BOTH walls. The tablet is registered to the first, so the second is
        // out of reach from it — even though the owner branch of the guard ignores query filters and
        // would otherwise find the row.
        await using var db = h.CreateContext();
        db.KioskWallId = h.WallId;

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => WallAdminGuard.EnsureWallAdminAsync(db, otherWallId, h.Owner.Id, CancellationToken.None));
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => WallAdminGuard.EnsureWallEditorAsync(db, otherWallId, h.Owner.Id, CancellationToken.None));

        Assert.False(await WallAdminGuard.IsWallAdminAsync(db, otherWallId, h.Owner.Id, CancellationToken.None));
        Assert.False(await WallAdminGuard.IsWallModeratorOrAboveAsync(db, otherWallId, h.Owner.Id, CancellationToken.None));

        // The kiosk's own wall keeps every bit of that authority — a locked product decision.
        Assert.True(await WallAdminGuard.IsWallAdminAsync(db, h.WallId, h.Owner.Id, CancellationToken.None));
        await WallAdminGuard.EnsureWallAdminAsync(db, h.WallId, h.Owner.Id, CancellationToken.None);
    }

    [Fact]
    public async Task WallAdminGuard_IsUntouchedForAnOrdinarySession()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWallId = await SeedSecondWallAsync(h, h.Owner.Id);

        // No kiosk stamp: the owner administers both walls exactly as before kiosk mode existed.
        await using var db = h.CreateContext();

        Assert.True(await WallAdminGuard.IsWallAdminAsync(db, h.WallId, h.Owner.Id, CancellationToken.None));
        Assert.True(await WallAdminGuard.IsWallAdminAsync(db, otherWallId, h.Owner.Id, CancellationToken.None));
    }

    [Fact]
    public async Task MaintenanceMode_IsRefusedOnAForeignWall_ButAllowedOnTheKiosksOwn()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var otherWallId = await SeedSecondWallAsync(h, h.Owner.Id);

        var walls = KioskWallService(h, h.WallId);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => walls.SetMaintenanceAsync(otherWallId, true));

        // Destructive wall administration on the tablet's OWN wall is deliberately still allowed.
        await walls.SetMaintenanceAsync(h.WallId, true);

        await using var db = h.CreateContext();
        Assert.True(await db.Walls.IgnoreQueryFilters().Where(w => w.Id == h.WallId).Select(w => w.UnderMaintenance).FirstAsync());
        Assert.False(await db.Walls.IgnoreQueryFilters().Where(w => w.Id == otherWallId).Select(w => w.UnderMaintenance).FirstAsync());
    }

    // ---------------------------------------------------------------------------------------
    // 3. Kiosk consent: a one-time PIN compromise must not become a permanent one.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task KioskConsent_CannotBeChangedFromTheTablet()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // Consent WITH a PIN, granted the proper way: from the member's own device.
        await h.KioskService.ConsentAsync(h.WallId, "4821");

        var kiosk = KioskService(h, h.WallId);

        // Re-consenting without a PIN is what would erase it, so both directions are refused.
        await Assert.ThrowsAsync<KioskRestrictedException>(() => kiosk.ConsentAsync(h.WallId, null));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => kiosk.RevokeConsentAsync(h.WallId));

        await using var db = h.CreateContext();
        var member = await db.WallMembers.IgnoreQueryFilters()
            .FirstAsync(m => m.WallId == h.WallId && m.UserId == h.Owner.Id);
        Assert.NotNull(member.KioskPinHash);
        Assert.NotNull(member.KioskConsentedAt);

        // And the PIN still guards the pick.
        Assert.True(await h.KioskService.VerifyPinAsync(h.WallId, h.Owner.Id, "4821"));
        Assert.False(await h.KioskService.VerifyPinAsync(h.WallId, h.Owner.Id, null));
    }

    [Fact]
    public async Task KioskConsent_IsUnchangedFromAnOrdinarySession()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        await h.KioskService.ConsentAsync(h.WallId, "4821");
        Assert.True(await h.KioskService.HasConsentedAsync(h.WallId));

        await h.KioskService.RevokeConsentAsync(h.WallId);
        Assert.False(await h.KioskService.HasConsentedAsync(h.WallId));
    }

    // ---------------------------------------------------------------------------------------
    // 4. The password seam that did not go through CurrentUserService.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task PasswordReset_AndSignup_AreRefusedFromAKiosk()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var logins = new PasswordLoginService(
            KioskFactory(h, h.WallId),
            h.PasswordService,
            StubKiosk(isKiosk: true, h.WallId));

        // /reset-password calls this directly, NOT CurrentUserService.SetPasswordAsync, so the
        // EnsureNotKiosk over there never covered it.
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => logins.ResetPasswordAsync(h.Owner.Id, "hunter2hunter2"));

        // The account half of the share-link escalation.
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => logins.CreateLocalUserAsync("stranger", "hunter2hunter2", "stranger@example.com"));

        await using var db = h.CreateContext();
        Assert.Null(await db.Users.Where(u => u.Id == h.Owner.Id).Select(u => u.PasswordHash).FirstAsync());
    }

    [Fact]
    public async Task PasswordReset_StillWorksForAnOrdinarySession()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var logins = new PasswordLoginService(h.DbContextFactory, h.PasswordService);
        await logins.ResetPasswordAsync(h.Owner.Id, "hunter2hunter2");

        await using var db = h.CreateContext();
        var hash = await db.Users.Where(u => u.Id == h.Owner.Id).Select(u => u.PasswordHash).FirstAsync();
        Assert.NotNull(hash);
        Assert.True(h.PasswordService.Verify(hash!, "hunter2hunter2"));
    }

    // ---------------------------------------------------------------------------------------
    // 5. /administration: the named policy has to carry the kiosk requirement itself.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AppAdminPolicy_DeniesAKioskSession_ByItself()
    {
        var policy = AuthenticationServices.BuildAppAdminPolicy(new AuthorizationPolicyBuilder());

        // The requirement must be ON this policy. A named policy replaces the default policy rather
        // than extending it, so inheriting it from BuildHumanPolicy is exactly what did not happen.
        Assert.Contains(policy.Requirements, r => r is KioskRouteRequirement);

        var kiosk = KioskPrincipal();
        var dashboard = typeof(Blocwerk.Web.Components.Pages.Administration.Dashboard);

        Assert.False(await SatisfiesKioskRequirementAsync(policy, kiosk, dashboard));

        // An ordinary admin is not touched by the kiosk requirement (the AppAdminRequirement is
        // evaluated separately, by its own handler, against the database).
        Assert.True(await SatisfiesKioskRequirementAsync(policy, OrdinaryPrincipal(), dashboard));
    }

    // ---------------------------------------------------------------------------------------
    // 6. The in-circuit gate: idle and revocation must end a LIVE circuit.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CircuitPolicy_EndsAKioskSessionOnceItHasBeenIdleTooLong()
    {
        var start = DateTimeOffset.UtcNow;

        // Under the window: the circuit keeps working.
        Assert.False(KioskCircuitPolicy.ShouldEndSession(
            isKioskSession: true, start, start.AddMinutes(29), credentialsRevoked: false));

        // Past it: every interactive write is refused, which is what did NOT happen before — the
        // cookie validator only runs on an HTTP request, and a live circuit makes none.
        Assert.True(KioskCircuitPolicy.ShouldEndSession(
            isKioskSession: true, start, start.AddMinutes(31), credentialsRevoked: false));

        // The in-circuit window is the same constant the cookie validator enforces, so the two
        // gates cannot drift apart.
        Assert.Equal(
            KioskSessionValidator.IsIdleExpired(start, start.AddMinutes(31)),
            KioskCircuitPolicy.ShouldEndSession(true, start, start.AddMinutes(31), false));
    }

    [Fact]
    public void CircuitPolicy_EndsAKioskSessionWhenItsCredentialsAreGone()
    {
        var now = DateTimeOffset.UtcNow;

        // Revoking the kiosk key or withdrawing consent ends a live session immediately, with no
        // idle time elapsed at all.
        Assert.True(KioskCircuitPolicy.ShouldEndSession(
            isKioskSession: true, now, now, credentialsRevoked: true));
    }

    [Fact]
    public void CircuitPolicy_NeverTouchesANonKioskCircuit()
    {
        var start = DateTimeOffset.UtcNow;

        // An ordinary login keeps its 8-hour sliding cookie and is never dropped by this gate — not
        // on idle time, and not even if the flag were somehow set.
        Assert.False(KioskCircuitPolicy.ShouldEndSession(
            isKioskSession: false, start, start.AddDays(1), credentialsRevoked: false));
        Assert.False(KioskCircuitPolicy.ShouldEndSession(
            isKioskSession: false, start, start.AddDays(1), credentialsRevoked: true));
    }

    [Fact]
    public void CircuitPolicy_RevalidatesOnAnInterval_RatherThanOnEveryClick()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(KioskCircuitPolicy.ShouldRevalidate(now, now));
        Assert.False(KioskCircuitPolicy.ShouldRevalidate(now, now.Add(KioskCircuitPolicy.RevalidationInterval).AddSeconds(-1)));
        Assert.True(KioskCircuitPolicy.ShouldRevalidate(now, now.Add(KioskCircuitPolicy.RevalidationInterval)));

        // Short enough that revoking a key empties the tablet while the admin is still standing at
        // it, and comfortably inside the idle window it backs up.
        Assert.True(KioskCircuitPolicy.RevalidationInterval < KioskClaims.IdleTimeout);
    }

    // ---------------------------------------------------------------------------------------
    // 7. Throttling: exponential backoff, and a cap that a round-robin cannot escape.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void PinThrottle_BacksOffExponentiallyAcrossConsecutiveBursts()
    {
        var registry = new KioskThrottleRegistry();
        var scope = KioskThrottleRegistry.PinScopes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())[0];
        var now = DateTimeOffset.UtcNow;

        var lockouts = new List<TimeSpan>();

        for (var burst = 0; burst < 4; burst++)
        {
            for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
            {
                registry.RegisterFailure(scope, now);
            }

            // Walk forward until it frees up; that distance is this burst's lockout.
            var served = TimeSpan.Zero;
            while (registry.IsLocked(scope, now + served))
            {
                served += TimeSpan.FromSeconds(30);
            }

            lockouts.Add(served);
            now += served;
        }

        // Each burst costs strictly more than the last, so the flat one-minute lockout that allowed
        // ~7,200 guesses a day against a 10,000-value space is gone.
        for (var i = 1; i < lockouts.Count; i++)
        {
            Assert.True(
                lockouts[i] > lockouts[i - 1],
                $"Burst {i + 1} was locked out for {lockouts[i]}, no longer than burst {i}'s {lockouts[i - 1]}.");
        }

        Assert.True(lockouts[^1] >= TimeSpan.FromMinutes(8));
    }

    [Fact]
    public void PinThrottle_ForgetsTheEscalationAfterALongQuietPeriod()
    {
        var registry = new KioskThrottleRegistry();
        var scope = KioskThrottleRegistry.PinScopes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())[0];
        var now = DateTimeOffset.UtcNow;

        for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
        {
            registry.RegisterFailure(scope, now);
        }

        // A member who fumbles their PIN once in a while must never accumulate an hour-long lockout,
        // so the doubling is forgiven once the device has been quiet.
        var muchLater = now + KioskThrottleRegistry.BurstMemory + TimeSpan.FromMinutes(1);
        Assert.False(registry.IsLocked(scope, muchLater));

        for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
        {
            registry.RegisterFailure(scope, muchLater);
        }

        Assert.True(registry.IsLocked(scope, muchLater));
        Assert.False(registry.IsLocked(scope, muchLater.Add(KioskThrottleRegistry.Lockout).AddSeconds(1)));
    }

    [Fact]
    public void PinThrottle_CapsADeviceAcrossEveryTargetItTries()
    {
        var registry = new KioskThrottleRegistry();
        var key = Guid.NewGuid();
        var device = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Round-robin: a fresh victim every five guesses, so the per-target counter never trips.
        for (var member = 0; member < 6; member++)
        {
            var scopes = KioskThrottleRegistry.PinScopes(key, device, Guid.NewGuid());
            for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
            {
                registry.RegisterFailure(scopes, now);
            }
        }

        // The device-wide counter catches it anyway — including for a member never tried before.
        Assert.True(registry.IsLocked(KioskThrottleRegistry.PinScopes(key, device, Guid.NewGuid()), now));

        // Another tablet is unaffected: this is a device cap, not a wall-wide outage.
        Assert.False(registry.IsLocked(
            KioskThrottleRegistry.PinScopes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), now));
    }

    /// <summary>
    /// A wall may register SEVERAL tablets with the same kiosk key. The device cap must key on the
    /// per-registration device id, or guessing at the tablet by the door locks out the one upstairs —
    /// a denial of service handed to every passer-by, which is exactly what the throttle exists to
    /// avoid.
    /// </summary>
    [Fact]
    public void PinThrottle_DoesNotLockOneTabletOutBecauseAnotherOnTheSameKeyWasGuessedAt()
    {
        var registry = new KioskThrottleRegistry();
        var sharedKey = Guid.NewGuid();
        var tabletA = Guid.NewGuid();
        var tabletB = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        for (var member = 0; member < 6; member++)
        {
            var scopes = KioskThrottleRegistry.PinScopes(sharedKey, tabletA, Guid.NewGuid());
            for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
            {
                registry.RegisterFailure(scopes, now);
            }
        }

        // The guessed-at tablet is capped...
        Assert.True(registry.IsLocked(
            KioskThrottleRegistry.PinScopes(sharedKey, tabletA, Guid.NewGuid()), now));

        // ...and its neighbour on the SAME key is not.
        Assert.False(registry.IsLocked(
            KioskThrottleRegistry.PinScopes(sharedKey, tabletB, Guid.NewGuid()), now));
    }

    [Fact]
    public void RegistrationThrottle_CapsGloballyRegardlessOfTheClientAddress()
    {
        var registry = new KioskThrottleRegistry();
        var now = DateTimeOffset.UtcNow;

        // X-Forwarded-For is honoured from any client app-wide, so an attacker rotates the address
        // and never trips the per-address counter. Each of these is a "different client".
        for (var i = 0; i < 20; i++)
        {
            registry.RegisterFailure(KioskThrottleRegistry.RegistrationScopes($"10.0.0.{i}"), now);
        }

        // The global scope keys on nothing the client can influence, so it catches it.
        Assert.True(registry.IsLocked(KioskThrottleRegistry.RegistrationScopes("203.0.113.7"), now));

        // Per-address counters are still per address, so one bad client is not the reason.
        Assert.False(registry.IsLocked(KioskThrottleRegistry.RegistrationScopes("203.0.113.7")[0], now));
    }

    // ---------------------------------------------------------------------------------------
    // 7b. The profile page is REACHABLE again; the account security behind it still is not.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// /profile used to be denied wholesale, which also denied /profile/{userId} — another member's
    /// public profile, i.e. every tap on a name in the members list or the leaderboard. The page is
    /// allowed now, so this pins down that allowing it moved NOTHING on the deliberate-exception
    /// list: the password, the second factor and API-key minting are refused by the services the
    /// page calls, and the pages that exist only to escalate are still refused by path and by type.
    /// </summary>
    [Fact]
    public async Task TheProfilePageIsReachableFromAKiosk_ButNoneOfItsAccountSecurityIs()
    {
        // Reachable: both routes, and the page type the in-circuit route gate sees.
        Assert.False(KioskRestrictions.IsBlockedPath(new PathString("/profile")));
        Assert.False(KioskRestrictions.IsBlockedPath(
            new PathString("/profile/11111111-1111-1111-1111-111111111111")));
        Assert.False(KioskRestrictions.IsBlockedPageType(typeof(Blocwerk.Web.Components.Pages.Profile)));

        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        var users = new CurrentUserService(
            new Blocwerk.Core.Configuration.BlocwerkSettings(),
            KioskFactory(h, h.WallId),
            Substitute.For<IPasswordLoginService>(),
            Substitute.For<ITotpService>(),
            kioskContext: StubKiosk(isKiosk: true, h.WallId));

        // The three account-security writes that live on that page. Each guard runs BEFORE the
        // service resolves a user or touches the database, which is why no session is needed here.
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => users.SetPasswordAsync("climber", "hunter2hunter2", null));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => users.BeginTotpEnrollmentAsync());
        await Assert.ThrowsAsync<KioskRestrictedException>(() => users.DisableTotpAsync("123456"));

        // And the fourth thing the page links to: minting a key. The inner service must not even be
        // reached — the refusal is the wrapper's, not a permission check that could go the other way.
        var inner = Substitute.For<IApiKeyService>();
        var keys = new KioskGuardedApiKeyService(inner, StubKiosk(isKiosk: true, h.WallId));

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => keys.CreateUserKeyAsync(h.Owner.Id, h.Owner.Id, "tablet key", null));
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => keys.CreateWallKeyAsync(h.WallId, h.Owner.Id, "tablet key", null));
        await inner.DidNotReceiveWithAnyArgs().CreateUserKeyAsync(default, default, null!, null);
        await inner.DidNotReceiveWithAnyArgs().CreateWallKeyAsync(default, default, null!, null);

        // The escalation-only surfaces the profile page used to hide behind its own refusal are
        // still refused in their own right, by path and by page type.
        Assert.True(KioskRestrictions.IsBlockedPath(new PathString("/settings/api-keys")));
        Assert.True(KioskRestrictions.IsBlockedPath(new PathString("/account/link")));
        Assert.True(KioskRestrictions.IsBlockedPath(new PathString("/account/password")));
        Assert.True(KioskRestrictions.IsBlockedPageType(
            typeof(Blocwerk.Web.Components.Pages.Settings.ApiKeys)));
    }

    /// <summary>
    /// The e-mail change is the one account-security write on that page with no service seam — it is
    /// written inline by the component — so its only gate is the inline check, and the widgets around
    /// it are hidden for a kiosk session. Both live in the .razor, where no runtime reflection can
    /// see them, so this reads the source. It exists because the page is reachable from a tablet now:
    /// deleting either line would silently restore exactly the hole /profile's denial used to cover.
    /// </summary>
    [Fact]
    public void TheProfilePageStillRefusesAnEmailChangeFromAKiosk()
    {
        var profile = Path.Combine(
            RepositoryRoot(), "src", "Blocwerk.Web", "Components", "Pages", "Profile.razor");
        Assert.True(File.Exists(profile), $"Profile.razor not found at '{profile}'.");

        var source = File.ReadAllText(profile);

        // The inline refusal at the write itself.
        Assert.Contains("private async Task SaveVerifiedEmailAsync", source, StringComparison.Ordinal);
        var write = source[source.IndexOf("private async Task SaveVerifiedEmailAsync", StringComparison.Ordinal)..];
        Assert.Contains("KioskContext.IsKiosk", write[..Math.Min(write.Length, 1200)], StringComparison.Ordinal);

        // And the defence in depth: the e-mail, password, second-factor and linked-account widgets
        // are not rendered at all for a kiosk session.
        Assert.Contains("@if (KioskContext.IsKiosk)", source, StringComparison.Ordinal);
        Assert.Contains("@if (!KioskContext.IsKiosk)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pasting TopLogger tokens is third-party credential entry, so it belongs on the member's own
    /// device rather than a public tablet. The token textareas live in one RenderFragment rendered
    /// from exactly two places — the "Update tokens" disclosure once connected, and the bare form
    /// when not — and both are behind a kiosk branch. Reading the source is the only way to see it:
    /// the gate is markup, not a service seam.
    /// </summary>
    [Fact]
    public void TheProfilePageDoesNotOfferTopLoggerTokenEntryFromAKiosk()
    {
        var profile = Path.Combine(
            RepositoryRoot(), "src", "Blocwerk.Web", "Components", "Pages", "Profile.razor");
        Assert.True(File.Exists(profile), $"Profile.razor not found at '{profile}'.");

        var source = File.ReadAllText(profile);
        Assert.Contains("<textarea id=\"tl-access\"", source, StringComparison.Ordinal);

        var disclosure = source.IndexOf("<details class=\"tl-token-edit\">", StringComparison.Ordinal);
        Assert.True(disclosure > 0, "The 'Update tokens' disclosure moved; re-check its kiosk gate.");
        Assert.Contains(
            "KioskContext.IsKiosk",
            source[Math.Max(0, disclosure - 400)..disclosure],
            StringComparison.Ordinal);

        // The not-yet-connected branch renders the bare form, and is gated the same way.
        Assert.Contains("else if (KioskContext.IsKiosk)", source, StringComparison.Ordinal);

        // Nothing on the page can leak a stored token either: the status record it renders holds none.
        foreach (var property in typeof(Blocwerk.Core.Services.TopLogger.TopLoggerStatus).GetProperties())
        {
            Assert.DoesNotContain("token", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", property.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Minting a wall API key and approving a kiosk pairing are both refused for a kiosk session at
    /// the service, so rendering their cards on a tablet only offers taps that fail. The cards are
    /// markup, so this reads the source like the checks above.
    /// </summary>
    [Fact]
    public void TheWallPageHidesTheApiKeyAndPairingCardsFromAKiosk()
    {
        var wall = Path.Combine(
            RepositoryRoot(), "src", "Blocwerk.Web", "Components", "Pages", "Walls", "WallDetail.razor");
        Assert.True(File.Exists(wall), $"WallDetail.razor not found at '{wall}'.");

        var source = File.ReadAllText(wall);
        foreach (var card in new[] { "<WallApiKeyPanel", "<WallKioskPairingPanel" })
        {
            var at = source.IndexOf(card, StringComparison.Ordinal);
            Assert.True(at > 0, $"{card} not found in WallDetail.razor.");
            Assert.Contains(
                "KioskContext.IsKiosk",
                source[Math.Max(0, at - 600)..at],
                StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        // <root>/test/Blocwerk.Core.Tests/KioskEscalationTests.cs
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
    }

    // ---------------------------------------------------------------------------------------
    // 8. The kiosk's actual job, which none of the above may break.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task AKioskCanStillDoItsJobOnItsOwnWall()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 3);

        var factory = KioskFactory(h, h.WallId);
        var boulders = new BoulderService(factory, h.CurrentUser, h.ActivityLog, NullLogger<BoulderService>.Instance);
        var attempts = new AttemptService(factory, h.CurrentUser, h.ActivityLog, NullLogger<AttemptService>.Instance);

        // Set a boulder from the tablet.
        var boulder = await boulders.CreateBoulderAsync(
            h.WallId, "Kiosk Problem", null, [new BoulderHoldInput(holds[0].Id), new BoulderHoldInput(holds[1].Id)]);
        Assert.NotEqual(Guid.Empty, boulder.Id);

        // Edit it from the tablet.
        await boulders.UpdateBoulderAsync(
            boulder.Id, "Kiosk Problem v2", null, [new BoulderHoldInput(holds[0].Id), new BoulderHoldInput(holds[2].Id)]);

        // Log an attempt from the tablet — the single most common thing a wall tablet is for.
        var attempt = await attempts.LogAttemptAsync(boulder.Id, AttemptType.Send);
        Assert.NotEqual(Guid.Empty, attempt.Id);

        await using var db = h.CreateContext();
        Assert.Equal("Kiosk Problem v2", await db.Boulders.Where(b => b.Id == boulder.Id).Select(b => b.Name).FirstAsync());
        Assert.Equal(1, await db.Attempts.CountAsync(a => a.BoulderId == boulder.Id));

        // And the wall itself is readable, admin actions included.
        var walls = KioskWallService(h, h.WallId);
        Assert.NotNull(await walls.GetWallAsync(h.WallId));
        await walls.SetMaintenanceAsync(h.WallId, true);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The production factory over the harness's connection, so every context the service under test
    /// creates is stamped with the kiosk's wall exactly as it would be behind the middleware.
    /// </summary>
    private static IDbContextFactory<BlocwerkDbContext> KioskFactory(WallTestHarness h, Guid kioskWallId)
    {
        return new KioskScopedDbContextFactory(
            new HarnessRootFactory(h.DbContextFactory),
            StubKiosk(isKiosk: true, kioskWallId));
    }

    private static IWallService KioskWallService(WallTestHarness h, Guid kioskWallId)
    {
        return new WallService(
            KioskFactory(h, kioskWallId),
            h.CurrentUser,
            h.HoldDetection,
            h.ImageAlignment,
            h.ActivityLog,
            NullLogger<WallService>.Instance,
            StubKiosk(isKiosk: true, kioskWallId));
    }

    private static IKioskService KioskService(WallTestHarness h, Guid kioskWallId)
    {
        return new KioskService(
            KioskFactory(h, kioskWallId),
            h.CurrentUser,
            h.PasswordService,
            NullLogger<KioskService>.Instance,
            StubKiosk(isKiosk: true, kioskWallId));
    }

    private static IKioskContext StubKiosk(bool isKiosk, Guid? wallId)
    {
        var context = Substitute.For<IKioskContext>();
        context.IsKiosk.Returns(isKiosk);
        context.KioskWallId.Returns(wallId);
        return context;
    }

    private static async Task<Guid> SeedSecondWallAsync(WallTestHarness h, Guid ownerId)
    {
        await using var db = h.CreateContext();
        var wall = new Wall
        {
            Name = "Second Wall",
            OwnerId = ownerId,
            Photo = [1],
            PhotoContentType = "image/jpeg",
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = ownerId, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    /// <summary>
    /// Evaluates ONLY the <see cref="KioskRouteRequirement"/> carried by <paramref name="policy"/>,
    /// against a route to <paramref name="pageType"/>. The other requirements on the policy have
    /// their own DI-registered handlers and a database behind them; this test is about whether the
    /// kiosk requirement is present and firing, not about who counts as an app admin.
    /// </summary>
    private static async Task<bool> SatisfiesKioskRequirementAsync(
        AuthorizationPolicy policy,
        ClaimsPrincipal principal,
        Type pageType)
    {
        var requirement = policy.Requirements.OfType<KioskRouteRequirement>().Single();
        var routeData = new RouteData(pageType, new Dictionary<string, object?>());
        var context = new AuthorizationHandlerContext([requirement], principal, routeData);

        await requirement.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static ClaimsPrincipal KioskPrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("uid", Guid.NewGuid().ToString()),
                new Claim(KioskClaims.KeyId, Guid.NewGuid().ToString()),
                new Claim(KioskClaims.WallId, Guid.NewGuid().ToString()),
                new Claim(KioskClaims.LastSeen, KioskClaims.FormatLastSeen(DateTimeOffset.UtcNow)),
            ],
            "Cookies"));
    }

    private static ClaimsPrincipal OrdinaryPrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("uid", Guid.NewGuid().ToString())],
            "Cookies"));
    }

    /// <summary>
    /// Stands in for <see cref="RootDbContextFactory"/>, which can only build a Postgres context from
    /// the app's own options. <see cref="KioskScopedDbContextFactory"/> only calls
    /// <c>CreateDbContext</c> on it.
    /// </summary>
    private sealed class HarnessRootFactory : RootDbContextFactory
    {
        private readonly IDbContextFactory<BlocwerkDbContext> inner;

        public HarnessRootFactory(IDbContextFactory<BlocwerkDbContext> inner)
            : base(new DbContextOptionsBuilder<BlocwerkDbContext>().Options)
        {
            this.inner = inner;
        }

        public override BlocwerkDbContext CreateDbContext() => inner.CreateDbContext();
    }
}
