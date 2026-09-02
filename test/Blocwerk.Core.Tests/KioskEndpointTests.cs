using System.Security.Claims;
using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The three kiosk endpoints, exercised end to end against a real database: registering a tablet,
/// picking a consenting member, and releasing the session.
/// </summary>
public class KioskEndpointTests
{
    [Fact]
    public async Task Register_WithAValidKioskKey_RegistersTheDeviceToThatWall()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, token) = await fixture.CreateKioskKeyAsync();

        var result = await fixture.Controller.Register(token);

        Assert.Equal($"/walls/{fixture.Harness.WallId}", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Equal(fixture.Harness.WallId, fixture.ReadWrittenRegistration()?.WallId);
    }

    [Theory]
    [InlineData(KeyKind.Unknown)]
    [InlineData(KeyKind.Revoked)]
    [InlineData(KeyKind.Expired)]
    [InlineData(KeyKind.WallScoped)]
    [InlineData(KeyKind.UserScoped)]
    [InlineData(KeyKind.Empty)]
    public async Task Register_RefusesAnythingThatIsNotALiveKioskKey(KeyKind kind)
    {
        using var fixture = await KioskFixture.CreateAsync();
        var token = await fixture.BuildTokenAsync(kind);

        var result = await fixture.Controller.Register(token);

        // One generic failure for every reason, and above all: no device registration is written.
        Assert.Equal("/oauth-select?kerror=1", Assert.IsType<RedirectResult>(result).Url);
        Assert.Null(fixture.ReadWrittenRegistration());
    }

    [Fact]
    public async Task Register_IsThrottledAfterRepeatedBadKeys()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, good) = await fixture.CreateKioskKeyAsync();

        for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
        {
            await fixture.Controller.Register("bwk_" + new string('a', 64));
        }

        // Even the RIGHT key is refused while the throttle is closed, so guessing cannot be resumed
        // by getting lucky on the next try.
        await fixture.Controller.Register(good);
        Assert.Null(fixture.ReadWrittenRegistration());
    }

    [Fact]
    public async Task ActAs_RequiresAValidDeviceRegistration()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var member = await fixture.AddConsentingMemberAsync("member@test", pin: null);

        // No device cookie at all.
        Assert.IsType<ForbidResult>(await fixture.Controller.ActAs(member.Id, null));

        // A cookie whose key has since been revoked: refused, and the registration is torn up.
        var (key, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);
        await fixture.Harness.ApiKeyService.RevokeAsync(key.Id, fixture.Harness.Owner.Id);

        Assert.IsType<ForbidResult>(await fixture.Controller.ActAs(member.Id, null));
        Assert.False(fixture.HasDeviceCookie());
    }

    [Fact]
    public async Task ActAs_RefusesAMemberWhoNeverConsented()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);

        var member = await fixture.Harness.AddMemberAsync("silent@test", WallRole.Member);

        await fixture.AssertRefusedAsync(member.Id, pin: null);
    }

    [Fact]
    public async Task ActAs_RefusesAConsentingUserOfADifferentWall()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);

        // Consenting, with a PIN, but on a wall this tablet is not registered to. The wall id used
        // for the check comes from the device cookie, so naming the user is not enough.
        var stranger = await fixture.AddConsentingMemberOnOtherWallAsync("stranger@test", pin: "4321");

        await fixture.AssertRefusedAsync(stranger.Id, pin: "4321");
    }

    [Fact]
    public async Task ActAs_RefusesTheWrongPin_AndAcceptsTheRightOne()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (key, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);

        var member = await fixture.AddConsentingMemberAsync("pinned@test", pin: "2468");

        await fixture.AssertRefusedAsync(member.Id, pin: "1357");
        await fixture.AssertRefusedAsync(member.Id, pin: null);

        var result = await fixture.Controller.ActAs(member.Id, "2468");

        Assert.Equal($"/walls/{fixture.Harness.WallId}", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.NotNull(fixture.SignedInPrincipal);
        Assert.Equal(member.Id.ToString(), fixture.SignedInPrincipal!.FindFirst("uid")?.Value);
        Assert.Equal(key.Id.ToString(), fixture.SignedInPrincipal.FindFirst(KioskClaims.KeyId)?.Value);
        Assert.Equal(fixture.Harness.WallId.ToString(), fixture.SignedInPrincipal.FindFirst(KioskClaims.WallId)?.Value);
        Assert.NotNull(fixture.SignedInPrincipal.ReadLastSeen());
    }

    [Fact]
    public async Task ActAs_AcceptsAConsentingUserWithNoPin_OnAnEmptyPin()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);

        var member = await fixture.AddConsentingMemberAsync("onetap@test", pin: null);

        Assert.IsType<LocalRedirectResult>(await fixture.Controller.ActAs(member.Id, string.Empty));
        Assert.Equal(member.Id.ToString(), fixture.SignedInPrincipal?.FindFirst("uid")?.Value);
    }

    [Fact]
    public async Task ActAs_IssuesAThirtyMinuteNonPersistentTicket()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);
        var member = await fixture.AddConsentingMemberAsync("timed@test", pin: null);

        await fixture.Controller.ActAs(member.Id, null);

        var properties = fixture.SignInProperties;
        Assert.NotNull(properties);
        Assert.False(properties!.IsPersistent);
        Assert.True(properties.AllowRefresh);
        Assert.NotNull(properties.ExpiresUtc);

        var window = properties.ExpiresUtc!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(window, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task ActAs_IsThrottledAfterRepeatedWrongPins()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);
        var member = await fixture.AddConsentingMemberAsync("guessed@test", pin: "2468");

        for (var attempt = 0; attempt < KioskThrottleRegistry.MaxAttempts; attempt++)
        {
            await fixture.Controller.ActAs(member.Id, "0000");
        }

        // The correct PIN is refused too while the throttle is closed.
        await fixture.Controller.ActAs(member.Id, "2468");
        Assert.Null(fixture.SignedInPrincipal);
    }

    [Fact]
    public async Task Release_EndsTheSessionButLeavesTheTabletRegistered()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);

        var result = await fixture.Controller.Release();

        Assert.Equal($"/walls/{fixture.Harness.WallId}", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.True(fixture.SignedOut);
        Assert.True(fixture.HasDeviceCookie());
    }

    [Fact]
    public async Task Unregister_ClearsTheDeviceRegistration()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (_, token) = await fixture.CreateKioskKeyAsync();
        await fixture.RegisterAsync(token);

        Assert.IsType<LocalRedirectResult>(await fixture.Controller.Unregister());
        Assert.False(fixture.HasDeviceCookie());
    }

    [Fact]
    public async Task CompletePairing_WritesTheDeviceRegistrationAndLandsOnTheWall()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (key, _) = await fixture.CreateKioskKeyAsync();

        // The tail of the pairing flow: an admin has already approved on some other device, and the
        // tablet now makes this one request on its OWN connection because a Blazor circuit has no
        // response to append a cookie to.
        var pairing = fixture.Pairings.Create()!;
        Assert.True(fixture.Pairings.TryApprove(pairing.Id, fixture.Harness.WallId, key.Id));

        var result = await fixture.Controller.CompletePairing(pairing.Id, pairing.ClaimTicket);

        Assert.Equal($"/walls/{fixture.Harness.WallId}", Assert.IsType<LocalRedirectResult>(result).Url);

        var registration = fixture.ReadWrittenRegistration();
        Assert.NotNull(registration);
        Assert.Equal(key.Id, registration.ApiKeyId);
        Assert.Equal(fixture.Harness.WallId, registration.WallId);
    }

    [Fact]
    public async Task CompletePairing_RefusesAKeyThatWasRevokedBetweenApprovalAndRedemption()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (key, _) = await fixture.CreateKioskKeyAsync();

        var pairing = fixture.Pairings.Create()!;
        Assert.True(fixture.Pairings.TryApprove(pairing.Id, fixture.Harness.WallId, key.Id));

        // An admin who approves and then thinks better of it. Without the re-check the tablet would
        // be handed a device cookie for a dead key and land on a wall page in a broken half-state —
        // Register has always re-read the key before writing the cookie, and this path must too.
        await fixture.Harness.ApiKeyService.RevokeAsync(key.Id, fixture.Harness.Owner.Id);

        AssertGenericPairingFailure(await fixture.Controller.CompletePairing(pairing.Id, pairing.ClaimTicket));
        Assert.False(fixture.HasDeviceCookie());
    }

    [Fact]
    public async Task CompletePairing_CannotBeReplayed()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (key, _) = await fixture.CreateKioskKeyAsync();

        var pairing = fixture.Pairings.Create()!;
        Assert.True(fixture.Pairings.TryApprove(pairing.Id, fixture.Harness.WallId, key.Id));

        Assert.IsType<LocalRedirectResult>(await fixture.Controller.CompletePairing(pairing.Id, pairing.ClaimTicket));

        // Redemption removes the entry inside a lock, so a resubmitted POST — or anything that
        // captured the body — gets the same nothing as an invented ticket.
        AssertGenericPairingFailure(await fixture.Controller.CompletePairing(pairing.Id, pairing.ClaimTicket));
    }

    [Fact]
    public async Task CompletePairing_RefusesAWrongTicketAnUnknownPairingAndAnUnapprovedOne()
    {
        using var fixture = await KioskFixture.CreateAsync();
        var (key, _) = await fixture.CreateKioskKeyAsync();

        var approved = fixture.Pairings.Create()!;
        Assert.True(fixture.Pairings.TryApprove(approved.Id, fixture.Harness.WallId, key.Id));

        // Somebody who watched the code being approved, but never held the tablet's claim ticket.
        AssertGenericPairingFailure(await fixture.Controller.CompletePairing(approved.Id, "not-the-ticket"));
        AssertGenericPairingFailure(await fixture.Controller.CompletePairing(approved.Id, null));
        AssertGenericPairingFailure(await fixture.Controller.CompletePairing(Guid.NewGuid(), approved.ClaimTicket));

        var pending = fixture.Pairings.Create()!;
        AssertGenericPairingFailure(await fixture.Controller.CompletePairing(pending.Id, pending.ClaimTicket));

        // None of that registered the device.
        Assert.False(fixture.HasDeviceCookie());

        // And the real tablet is still able to finish, so a failed guess is not a denial of service.
        Assert.IsType<LocalRedirectResult>(await fixture.Controller.CompletePairing(approved.Id, approved.ClaimTicket));
    }

    /// <summary>
    /// Every way a redemption can fail lands on the same place with the same marker: expired,
    /// unknown, unapproved, wrong ticket, already used. Nothing distinguishes them.
    /// </summary>
    private static void AssertGenericPairingFailure(IActionResult result)
    {
        Assert.Equal("/kiosk/pair?perror=1", Assert.IsType<LocalRedirectResult>(result).Url);
    }

    public enum KeyKind
    {
        Unknown,
        Revoked,
        Expired,
        WallScoped,
        UserScoped,
        Empty,
    }

    /// <summary>Wires a real <see cref="KioskController"/> onto the SQLite test harness.</summary>
    private sealed class KioskFixture : IDisposable
    {
        private readonly IDataProtectionProvider dataProtection = new EphemeralDataProtectionProvider();
        private KioskDeviceCookie deviceCookie = null!;

        public WallTestHarness Harness { get; private set; } = null!;

        public KioskController Controller { get; private set; } = null!;

        /// <summary>The pairing store the controller's completion endpoint redeems out of.</summary>
        public KioskPairingRegistry Pairings { get; } = new();

        public DefaultHttpContext HttpContext { get; private set; } = null!;

        public ClaimsPrincipal? SignedInPrincipal { get; private set; }

        public AuthenticationProperties? SignInProperties { get; private set; }

        public bool SignedOut { get; private set; }

        public static async Task<KioskFixture> CreateAsync()
        {
            var fixture = new KioskFixture();
            fixture.Harness = new WallTestHarness();
            await fixture.Harness.SeedWallAsync();
            fixture.deviceCookie = new KioskDeviceCookie(fixture.dataProtection);

            var authentication = Substitute.For<IAuthenticationService>();
            authentication
                .SignInAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<AuthenticationProperties>())
                .Returns(callInfo =>
                {
                    fixture.SignedInPrincipal = callInfo.ArgAt<ClaimsPrincipal>(2);
                    fixture.SignInProperties = callInfo.ArgAt<AuthenticationProperties>(3);
                    return Task.CompletedTask;
                });
            authentication
                .SignOutAsync(Arg.Any<HttpContext>(), Arg.Any<string>(), Arg.Any<AuthenticationProperties>())
                .Returns(_ =>
                {
                    fixture.SignedOut = true;
                    return Task.CompletedTask;
                });

            var services = new ServiceCollection();
            services.AddSingleton(authentication);

            fixture.HttpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };

            var kioskContext = Substitute.For<IKioskContext>();
            kioskContext.IsKiosk.Returns(_ => fixture.HasDeviceCookie());

            fixture.Controller = new KioskController(
                fixture.Harness.ApiKeyService,
                fixture.Harness.KioskService,
                fixture.Harness.CurrentUser,
                kioskContext,
                fixture.deviceCookie,
                new KioskKeyValidator(fixture.Harness.DbContextFactory),
                new KioskThrottleRegistry(),
                fixture.Pairings,
                NullLogger<KioskController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = fixture.HttpContext },
            };

            return fixture;
        }

        public Task<(ApiKey Key, string Token)> CreateKioskKeyAsync()
        {
            return Harness.ApiKeyService.CreateKioskKeyAsync(Harness.WallId, Harness.Owner.Id, "Tablet", null);
        }

        public async Task<string> BuildTokenAsync(KeyKind kind)
        {
            switch (kind)
            {
                case KeyKind.Unknown:
                    return "bwk_" + new string('0', 64);
                case KeyKind.Empty:
                    return string.Empty;
                case KeyKind.Revoked:
                    var (revoked, revokedToken) = await CreateKioskKeyAsync();
                    await Harness.ApiKeyService.RevokeAsync(revoked.Id, Harness.Owner.Id);
                    return revokedToken;
                case KeyKind.Expired:
                    var (_, expiredToken) = await Harness.ApiKeyService.CreateKioskKeyAsync(
                        Harness.WallId, Harness.Owner.Id, "Stale tablet", DateTimeOffset.UtcNow.AddMinutes(-1));
                    return expiredToken;
                case KeyKind.WallScoped:
                    var (_, wallToken) = await Harness.ApiKeyService.CreateWallKeyAsync(
                        Harness.WallId, Harness.Owner.Id, "Sensor", null);
                    return wallToken;
                default:
                    var (_, userToken) = await Harness.ApiKeyService.CreateUserKeyAsync(
                        Harness.Owner.Id, Harness.Owner.Id, "Personal", null);
                    return userToken;
            }
        }

        /// <summary>Registers the device and moves the resulting cookie onto the request.</summary>
        public async Task RegisterAsync(string token)
        {
            await Controller.Register(token);
            PromoteResponseCookieToRequest();
        }

        public async Task<User> AddConsentingMemberAsync(string identifier, string? pin)
        {
            var user = await Harness.AddMemberAsync(identifier, WallRole.Member);
            await ConsentAsync(user, Harness.WallId, pin);
            Harness.CurrentUser.GetUserByIdAsync(user.Id).Returns(user);
            return user;
        }

        public async Task<User> AddConsentingMemberOnOtherWallAsync(string identifier, string? pin)
        {
            var user = new User { Identifier = identifier, DisplayName = identifier };
            Guid otherWallId;

            await using (var db = Harness.CreateContext())
            {
                var wall = new Wall
                {
                    Name = "Other Wall",
                    OwnerId = Harness.Owner.Id,
                    Photo = [1],
                    PhotoContentType = "image/jpeg",
                };
                db.Walls.Add(wall);
                db.Users.Add(user);
                db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = user.Id, Role = WallRole.Member });
                await db.SaveChangesAsync();
                otherWallId = wall.Id;
            }

            await ConsentAsync(user, otherWallId, pin);
            Harness.CurrentUser.GetUserByIdAsync(user.Id).Returns(user);
            return user;
        }

        public async Task AssertRefusedAsync(Guid userId, string? pin)
        {
            SignedInPrincipal = null;

            var result = await Controller.ActAs(userId, pin);

            // Back to the pick that failed, so the numpad can simply be retyped. Still one generic
            // marker for every failure reason.
            Assert.Equal(
                $"/kiosk/users/{userId}?kiosk_pin_error=1",
                Assert.IsType<LocalRedirectResult>(result).Url);
            Assert.Null(SignedInPrincipal);
        }

        public KioskDeviceRegistration? ReadWrittenRegistration()
        {
            PromoteResponseCookieToRequest();
            return deviceCookie.Read(HttpContext);
        }

        public bool HasDeviceCookie()
        {
            PromoteResponseCookieToRequest();
            return deviceCookie.Read(HttpContext) is not null;
        }

        public void Dispose()
        {
            Harness.Dispose();
        }

        private async Task ConsentAsync(User user, Guid wallId, string? pin)
        {
            var previous = Harness.ActingUser;
            Harness.ActingUser = user;
            try
            {
                await Harness.KioskService.ConsentAsync(wallId, pin);
            }
            finally
            {
                Harness.ActingUser = previous;
            }
        }

        /// <summary>
        /// Mirrors what a browser does between two requests: whatever Set-Cookie the response carried
        /// becomes the next request's cookie. A deletion (empty value) clears it.
        /// </summary>
        private void PromoteResponseCookieToRequest()
        {
            foreach (var header in HttpContext.Response.Headers.SetCookie)
            {
                if (header is null || !header.StartsWith($"{KioskDeviceCookie.Name}=", StringComparison.Ordinal))
                {
                    continue;
                }

                var value = header[(KioskDeviceCookie.Name.Length + 1)..].Split(';')[0];
                HttpContext.Request.Headers.Cookie = string.IsNullOrEmpty(value)
                    ? string.Empty
                    : $"{KioskDeviceCookie.Name}={value}";
            }

            HttpContext.Response.Headers.Remove("Set-Cookie");
        }
    }
}
