using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Kiosk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The kiosk auth layer's decision logic: the device cookie, how a session is recognised as a kiosk,
/// the 30-minute idle window, and the deny-list.
/// </summary>
public class KioskAuthTests
{
    private static readonly Guid WallA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid WallB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid KeyA = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static KioskDeviceRegistration Registration(Guid apiKeyId, Guid wallId)
    {
        return new KioskDeviceRegistration(apiKeyId, wallId, Guid.NewGuid(), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void DeviceCookie_RoundTrips_AndIsUnreadableOnceTampered()
    {
        var cookie = new KioskDeviceCookie(new EphemeralDataProtectionProvider());
        var write = new DefaultHttpContext();

        cookie.Write(write, KeyA, WallA);

        var raw = ExtractCookieValue(write, KioskDeviceCookie.Name);
        Assert.NotNull(raw);
        Assert.DoesNotContain(WallA.ToString("N"), raw);

        var read = cookie.Read(WithCookie(raw!));
        Assert.NotNull(read);
        Assert.Equal(KeyA, read!.ApiKeyId);
        Assert.Equal(WallA, read.WallId);

        // The payload now carries a per-registration device id and an issue timestamp, so a copied
        // cookie can be aged out and two tablets on one key are distinguishable.
        Assert.NotEqual(Guid.Empty, read.DeviceId);
        Assert.True((DateTimeOffset.UtcNow - read.IssuedAt).Duration() < TimeSpan.FromMinutes(5));

        // A single flipped character must not decode to anything at all.
        var tampered = raw![..^2] + (raw[^1] == 'A' ? "B" : "A");
        Assert.Null(cookie.Read(WithCookie(tampered)));

        // A different key ring cannot read it either.
        var stranger = new KioskDeviceCookie(new EphemeralDataProtectionProvider());
        Assert.Null(stranger.Read(WithCookie(raw)));
    }

    [Fact]
    public void DeviceCookie_WritesADistinctDeviceIdPerRegistration()
    {
        var cookie = new KioskDeviceCookie(new EphemeralDataProtectionProvider());

        var first = cookie.Write(new DefaultHttpContext(), KeyA, WallA);
        var second = cookie.Write(new DefaultHttpContext(), KeyA, WallA);

        // Same key, same wall, two tablets: identifiable apart in logs, and keyable by a per-device
        // revocation store if one is ever added.
        Assert.NotEqual(first.DeviceId, second.DeviceId);
    }

    [Fact]
    public void DeviceCookie_RefusesAnImplausiblyOldOrFutureDatedRegistration()
    {
        var cookie = new KioskDeviceCookie(new EphemeralDataProtectionProvider());
        var context = new DefaultHttpContext();
        var issued = cookie.Write(context, KeyA, WallA);
        var raw = ExtractCookieValue(context, KioskDeviceCookie.Name)!;

        // Inside the five-year lifetime the value still reads.
        Assert.NotNull(cookie.Read(WithCookie(raw), issued.IssuedAt.AddYears(4)));

        // Past it, it does not — the Expires attribute only binds the BROWSER, so a value copied off
        // a device would otherwise be a bearer token with no end date at all.
        Assert.Null(cookie.Read(WithCookie(raw), issued.IssuedAt.AddYears(6)));

        // A stamp from the future means a clock nobody should trust.
        Assert.Null(cookie.Read(WithCookie(raw), issued.IssuedAt.AddDays(-2)));
    }

    [Fact]
    public void DeviceCookie_RefusesAPayloadWithoutTheVersionMarker()
    {
        // A registration written before the timestamp existed cannot have its age checked, so it is
        // discarded rather than trusted; the tablet is registered again with the key.
        var provider = new EphemeralDataProtectionProvider();
        var cookie = new KioskDeviceCookie(provider);
        var legacy = provider.CreateProtector("blocwerk.kiosk.device").Protect($"{KeyA:N}:{WallA:N}");

        Assert.Null(cookie.Read(WithCookie(legacy)));
    }

    [Fact]
    public void DeviceCookie_IsHttpOnlyAndLaxAndLongLived()
    {
        var cookie = new KioskDeviceCookie(new EphemeralDataProtectionProvider());
        var context = new DefaultHttpContext();

        cookie.Write(context, KeyA, WallA);

        var header = context.Response.Headers.SetCookie.ToString();
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires=", header, StringComparison.OrdinalIgnoreCase);

        // Plain http (as in development) must not get the Secure flag, or the cookie is dropped.
        Assert.DoesNotContain("secure", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviceCookie_IsSecure_OnHttps()
    {
        var cookie = new KioskDeviceCookie(new EphemeralDataProtectionProvider());
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";

        cookie.Write(context, KeyA, WallA);

        Assert.Contains("secure", context.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KioskContext_ResolvesFromClaims_FromCookie_AndFromNeither()
    {
        // The acting session's claims alone.
        var fromClaims = Resolve(KioskPrincipal(KeyA, WallA, DateTimeOffset.UtcNow), registration: null);
        Assert.True(fromClaims.IsKiosk);
        Assert.Equal(WallA, fromClaims.KioskWallId);
        Assert.Equal(KeyA, fromClaims.KioskApiKeyId);

        // The device cookie alone: an anonymous tablet is still a kiosk.
        var fromCookie = Resolve(principal: null, Registration(KeyA, WallA));
        Assert.True(fromCookie.IsKiosk);
        Assert.Equal(WallA, fromCookie.KioskWallId);

        // An ordinary browser is not a kiosk, and neither is an ordinary signed-in session.
        Assert.False(Resolve(principal: null, registration: null).IsKiosk);
        Assert.False(Resolve(OrdinaryPrincipal(), registration: null).IsKiosk);
    }

    [Fact]
    public void KioskContext_CapsToNothing_WhenClaimsAndCookieDisagreeOnTheWall()
    {
        var context = Resolve(
            KioskPrincipal(KeyA, WallA, DateTimeOffset.UtcNow),
            Registration(KeyA, WallB));

        // Still a kiosk (so every restriction applies) but scoped to a wall that matches nothing,
        // rather than picking whichever source happens to be more permissive.
        Assert.True(context.IsKiosk);
        Assert.Equal(Guid.Empty, context.KioskWallId);
    }

    [Fact]
    public void KioskContext_TreatsAnOrdinaryLoginOnARegisteredTabletAsAKiosk()
    {
        var context = Resolve(OrdinaryPrincipal(), Registration(KeyA, WallA));

        Assert.True(context.IsKiosk);
        Assert.Equal(WallA, context.KioskWallId);
    }

    /// <summary>
    /// /account/callback serves TWO flows. The link/merge branch must be refused for a kiosk session
    /// — attaching a provider identity outlives the 30 minutes by years — while the ORDINARY OAuth
    /// sign-in completion on the same route must still work, or a wall admin cannot sign in at the
    /// tablet at all. Denying the path refused both, which is the bug this covers.
    /// </summary>
    [Fact]
    public void AccountCallback_RefusesTheLinkBranchOnAKiosk_ButStillCompletesOrdinarySignIn()
    {
        // Both shapes of kiosk session: acting-as (claims) and an ordinary login on a registered
        // tablet (device cookie only). Neither may link.
        Assert.True(KioskRestrictions.IsBlockedAccountLink(
            Resolve(KioskPrincipal(KeyA, WallA, DateTimeOffset.UtcNow), null)));
        Assert.True(KioskRestrictions.IsBlockedAccountLink(
            Resolve(OrdinaryPrincipal(), Registration(KeyA, WallA))));

        // An ordinary browser is untouched: linking is a normal thing to do off a tablet.
        Assert.False(KioskRestrictions.IsBlockedAccountLink(Resolve(OrdinaryPrincipal(), null)));

        // A host that never registered IKioskContext hands the controller a null. Nothing to
        // restrict, and the link must not be refused for every non-kiosk user in that case.
        Assert.False(KioskRestrictions.IsBlockedAccountLink(null));

        // And the route itself stays reachable, so the sign-in completion is NOT a dead end — while
        // the half that only ever starts a link stays denied outright.
        Assert.False(KioskRestrictions.IsBlockedPath(new PathString("/account/callback")));
        Assert.True(KioskRestrictions.IsBlockedPath(new PathString("/account/link")));
        Assert.True(KioskRestrictions.IsBlockedPath(new PathString("/account/external")));
    }

    [Fact]
    public void IdleWindow_ExpiresAfterThirtyMinutes_AndSlidesOnActivity()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.False(KioskSessionValidator.IsIdleExpired(now.AddMinutes(-29), now));
        Assert.False(KioskSessionValidator.IsIdleExpired(now.AddMinutes(-30), now));
        Assert.True(KioskSessionValidator.IsIdleExpired(now.AddMinutes(-30).AddSeconds(-1), now));

        // Activity inside the window slides it: the stamp is only rewritten once it is stale enough
        // to be worth re-issuing the cookie for, which is what bounds the write rate.
        Assert.False(KioskSessionValidator.ShouldSlide(now.AddSeconds(-5), now));
        Assert.True(KioskSessionValidator.ShouldSlide(now.AddMinutes(-2), now));

        var slid = KioskSessionValidator.WithLastSeen(KioskPrincipal(KeyA, WallA, now.AddMinutes(-20)), now);
        Assert.Equal(now.ToUnixTimeSeconds(), slid.ReadLastSeen()!.Value.ToUnixTimeSeconds());
        Assert.False(KioskSessionValidator.IsIdleExpired(slid.ReadLastSeen()!.Value, now.AddMinutes(29)));
    }

    [Fact]
    public void SlidingTheWindow_KeepsEveryOtherClaim()
    {
        var now = DateTimeOffset.UtcNow;
        var principal = KioskPrincipal(KeyA, WallA, now.AddMinutes(-5));

        var slid = KioskSessionValidator.WithLastSeen(principal, now);

        Assert.Equal("user-1", slid.FindFirst("uid")?.Value);
        Assert.Equal(KeyA.ToString(), slid.FindFirst(KioskClaims.KeyId)?.Value);
        Assert.Equal(WallA.ToString(), slid.FindFirst(KioskClaims.WallId)?.Value);
        Assert.Single(slid.FindAll(KioskClaims.LastSeen));
        Assert.True(slid.Identity?.IsAuthenticated);
    }

    [Fact]
    public void OrdinarySessions_AreNotKioskPrincipals()
    {
        // This one predicate is what makes the idle validator a no-op for every normal login, so the
        // 8-hour sliding cookie is untouched.
        Assert.False(OrdinaryPrincipal().IsKioskPrincipal());
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity()).IsKioskPrincipal());
        Assert.True(KioskPrincipal(KeyA, WallA, DateTimeOffset.UtcNow).IsKioskPrincipal());
    }

    [Theory]

    // Account takeover, credential minting and app-wide administration.
    [InlineData("/settings/api-keys", true)]
    [InlineData("/administration", true)]
    [InlineData("/account/link", true)]

    // The gaps the allow-list closed: /account/link was blocked but the OAuth link actually
    // EXECUTES on /account/external; the password can be set by two more routes; and an account
    // plus a redeemed share link is permanent membership.
    [InlineData("/account/external", true)]
    [InlineData("/account/password", true)]
    [InlineData("/forgot-password", true)]
    [InlineData("/reset-password", true)]
    [InlineData("/signup", true)]
    [InlineData("/join/abc123", true)]
    [InlineData("/walls/create", true)]

    // Anything nobody has thought about is refused too, which is the point of inverting the list.
    [InlineData("/some/page/added/next/year", true)]
    [InlineData("/profiles", true)]

    // The kiosk's actual job, which must keep working.
    [InlineData("/", false)]
    [InlineData("/walls", false)]
    [InlineData("/walls/11111111-1111-1111-1111-111111111111", false)]
    [InlineData("/walls/11111111-1111-1111-1111-111111111111/boulders/create", false)]
    [InlineData("/walls/11111111-1111-1111-1111-111111111111/boulders/22222222-2222-2222-2222-222222222222", false)]
    [InlineData("/api/offline/actions", false)]
    [InlineData("/api/walls/11111111-1111-1111-1111-111111111111/photo", false)]
    [InlineData("/media/walls/11111111-1111-1111-1111-111111111111/gallery/wall/22222222-2222-2222-2222-222222222222", false)]
    [InlineData("/kiosk/act-as", false)]
    [InlineData("/kiosk/users", false)]
    [InlineData("/account/logout", false)]
    [InlineData("/account/login", false)]

    // Ordinary OAuth sign-in COMPLETES here. Denying the path made every allowed sign-in surface a
    // dead end and left a wall admin unable to sign in at a registered tablet; the link/merge half of
    // the route refuses itself instead (see IsBlockedAccountLink).
    [InlineData("/account/callback", false)]
    [InlineData("/oauth-select", false)]
    [InlineData("/tools/image-stitcher", false)]
    [InlineData("/guides/homewalls/volumes", false)]
    [InlineData("/activity", false)]
    [InlineData("/training/hangboard", false)]

    // Profiles. Denying this path denied another member's PUBLIC profile too, so every tap on a name
    // in the members list or the leaderboard bounced to ?kiosk_blocked=1. The account-security half
    // of the page is refused at the services beneath it, not by the path.
    [InlineData("/profile", false)]
    [InlineData("/profile/11111111-1111-1111-1111-111111111111", false)]
    [InlineData("/PROFILE", false)]

    // Static assets and framework plumbing, which the middleware also sees.
    [InlineData("/_blazor", false)]
    [InlineData("/_framework/blazor.web.js", false)]
    [InlineData("/css/app.css", false)]
    [InlineData("/js/kiosk-idle.js", false)]
    [InlineData("/icons/icon-192.png", false)]
    [InlineData("/manifest.webmanifest", false)]
    public void AllowList_PermitsTheKiosksJob_AndRefusesEverythingElse(string path, bool blocked)
    {
        Assert.Equal(blocked, KioskRestrictions.IsBlockedPath(new PathString(path)));
    }

    [Fact]
    public void AllowList_RefusesEveryDeniedPath_EvenUnderAnAllowedPrefix()
    {
        // /walls and the sign-in routes are allowed wholesale; these sit underneath them and must
        // still be refused, which is the only reason the denied list survived the inversion.
        foreach (var denied in KioskRestrictions.DeniedPaths)
        {
            Assert.True(
                KioskRestrictions.IsBlockedPath(new PathString(denied)),
                $"Denied kiosk path '{denied}' is reachable.");
            Assert.True(
                KioskRestrictions.IsBlockedPath(new PathString(denied + "/child")),
                $"Denied kiosk path '{denied}' is reachable one segment deeper.");
        }
    }

    [Fact]
    public void AllowList_NamesPagesThatActuallyExist()
    {
        // The page names are matched as strings because Blocwerk.Authentication cannot reference
        // Blocwerk.Web. This test is what stops a rename from silently CHANGING a page's status —
        // in either direction, now that the list is an allow-list.
        var webAssembly = typeof(Blocwerk.Web.Program).Assembly;

        foreach (var name in KioskRestrictions.AllowedPageTypes)
        {
            var type = webAssembly.GetType(name);
            Assert.True(type is not null, $"Kiosk-allowed page '{name}' no longer exists.");
            Assert.False(KioskRestrictions.IsBlockedPageType(type));
        }

        foreach (var name in KioskRestrictions.RefusedPageTypes)
        {
            var type = webAssembly.GetType(name);
            Assert.True(type is not null, $"Kiosk-refused page '{name}' no longer exists.");
            Assert.True(KioskRestrictions.IsBlockedPageType(type));
        }

        Assert.False(KioskRestrictions.IsBlockedPageType(typeof(Blocwerk.Web.Components.Pages.Walls.WallDetail)));

        // Profile is ALLOWED, for /profile/{userId}: a member's public profile behind their name.
        // Nothing about that allows an account-security change — see KioskCannotChangeAccountSecurity.
        Assert.False(KioskRestrictions.IsBlockedPageType(typeof(Blocwerk.Web.Components.Pages.Profile)));
        Assert.True(KioskRestrictions.IsBlockedPageType(typeof(Blocwerk.Web.Components.Pages.Settings.ApiKeys)));
        Assert.True(KioskRestrictions.IsBlockedPageType(typeof(Blocwerk.Web.Components.Pages.Signup)));
        Assert.True(KioskRestrictions.IsBlockedPageType(typeof(Blocwerk.Web.Components.Pages.Join)));
        Assert.True(KioskRestrictions.IsBlockedPageType(typeof(Blocwerk.Web.Components.Pages.Walls.WallCreate)));

        // A null page type is not a page at all — the route gates also evaluate the default policy
        // for HTTP endpoints, and refusing there would break every [Authorize] endpoint in the app.
        Assert.False(KioskRestrictions.IsBlockedPageType(null));
    }

    [Fact]
    public void AllowList_ClassifiesEveryRoutablePageInTheApp()
    {
        // The reason the inversion is maintainable. Every @page component in Blocwerk.Web must
        // appear on exactly one of the two lists, so adding a page FORCES a decision about whether
        // a public tablet may reach it — instead of defaulting to reachable (the old deny-list) or
        // to a silently broken page (an allow-list nobody remembers to update).
        var routable = typeof(Blocwerk.Web.Program).Assembly
            .GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(t)
                        && t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: true).Length > 0)
            .Select(t => t.FullName!)
            .ToList();

        Assert.NotEmpty(routable);

        var classified = KioskRestrictions.AllowedPageTypes
            .Concat(KioskRestrictions.RefusedPageTypes)
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = routable.Where(name => !classified.Contains(name)).ToList();
        Assert.True(
            unclassified.Count == 0,
            "These routable pages are on neither KioskRestrictions list — decide whether a kiosk "
            + "tablet may reach them and add each to AllowedPageTypes or RefusedPageTypes: "
            + string.Join(", ", unclassified));

        // And nothing is on both, or on a list without being routable any more.
        var stale = classified.Where(name => !routable.Contains(name)).ToList();
        Assert.True(stale.Count == 0, "No longer routable: " + string.Join(", ", stale));

        Assert.Equal(
            KioskRestrictions.AllowedPageTypes.Count + KioskRestrictions.RefusedPageTypes.Count,
            classified.Count);
    }

    [Fact]
    public void EveryRefusedPageIsActuallyRefusedByAGateAtRuntime()
    {
        // Being on RefusedPageTypes does not, by itself, refuse anything: IsBlockedPageType is
        // consulted only by the route gate, and the route gate runs as part of the DEFAULT
        // authorization policy, which AuthorizeRouteView evaluates only for a page carrying
        // IAuthorizeData — there is no FallbackPolicy, on purpose. So a listed page is genuinely
        // unreachable from a tablet only if at least one of the two runtime gates can see it:
        //
        //   [Authorize] on the page  -> the route gate evaluates, in-circuit navigation included, or
        //   every route on DeniedPaths -> the middleware refuses the request before any page runs.
        //
        // Pages that must stay reachable while signed OUT (signup, password reset, redeeming a share
        // link) cannot take the first and are covered by the second. The classification test above
        // cannot tell either apart from a page that is merely listed, which is how KioskApprove sat
        // on this list while a registered tablet could open it.
        var assembly = typeof(Blocwerk.Web.Program).Assembly;

        var ungated = new List<string>();
        foreach (var name in KioskRestrictions.RefusedPageTypes)
        {
            var type = assembly.GetType(name);
            Assert.NotNull(type);

            var authorized = type
                .GetCustomAttributes(inherit: true)
                .OfType<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Any();

            var routes = type
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), inherit: true)
                .Cast<Microsoft.AspNetCore.Components.RouteAttribute>()
                .Select(r => r.Template)
                .ToList();

            var denied = routes.Count > 0
                && routes.TrueForAll(template => KioskRestrictions.IsBlockedPath(new PathString(template)));

            if (!authorized && !denied)
            {
                ungated.Add(name);
            }
        }

        Assert.True(
            ungated.Count == 0,
            "These pages are on KioskRestrictions.RefusedPageTypes but nothing refuses them at "
            + "runtime — they carry no [Authorize]-derived attribute (so the kiosk route gate never "
            + "evaluates) and their routes are not on DeniedPaths (so the middleware waves them "
            + "through). A kiosk tablet can open them: " + string.Join(", ", ungated));
    }

    [Fact]
    public async Task RouteRequirement_BlocksBlockedPagesForAKiosk_AndNothingElse()
    {
        var kiosk = KioskPrincipal(KeyA, WallA, DateTimeOffset.UtcNow);
        var ordinary = OrdinaryPrincipal();

        // A kiosk session routing to a deny-listed page in-circuit, where no middleware ever runs.
        Assert.False(await EvaluateRouteAsync(kiosk, typeof(Blocwerk.Web.Components.Pages.Settings.ApiKeys)));
        Assert.False(await EvaluateRouteAsync(kiosk, typeof(Blocwerk.Web.Components.Pages.Administration.Dashboard)));

        // The profile page routes in-circuit now, which is what makes tapping a member's name work.
        Assert.True(await EvaluateRouteAsync(kiosk, typeof(Blocwerk.Web.Components.Pages.Profile)));

        // The wall itself stays fully open — a kiosk session keeps the picked user's wall authority.
        Assert.True(await EvaluateRouteAsync(kiosk, typeof(Blocwerk.Web.Components.Pages.Walls.WallDetail)));

        // An ordinary session is not touched, on any page.
        Assert.True(await EvaluateRouteAsync(ordinary, typeof(Blocwerk.Web.Components.Pages.Profile)));

        // A non-route resource (an HTTP endpoint evaluating the same default policy) is left alone.
        var requirement = new KioskRouteRequirement();
        var endpointContext = new AuthorizationHandlerContext([requirement], kiosk, resource: null);
        await requirement.HandleAsync(endpointContext);
        Assert.True(endpointContext.HasSucceeded);
    }

    private static async Task<bool> EvaluateRouteAsync(ClaimsPrincipal principal, Type pageType)
    {
        var requirement = new KioskRouteRequirement();
        var routeData = new Microsoft.AspNetCore.Components.RouteData(pageType, new Dictionary<string, object?>());
        var context = new AuthorizationHandlerContext([requirement], principal, routeData);

        await requirement.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static KioskContext Resolve(ClaimsPrincipal? principal, KioskDeviceRegistration? registration)
    {
        var context = new KioskContext(new HttpContextAccessor(), new KioskDeviceCookie(new EphemeralDataProtectionProvider()));
        context.Apply(principal, registration);
        return context;
    }

    private static ClaimsPrincipal KioskPrincipal(Guid keyId, Guid wallId, DateTimeOffset lastSeen)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "Climber"),
                new Claim("uid", "user-1"),
                new Claim(KioskClaims.KeyId, keyId.ToString()),
                new Claim(KioskClaims.WallId, wallId.ToString()),
                new Claim(KioskClaims.LastSeen, KioskClaims.FormatLastSeen(lastSeen)),
            ],
            "Cookies"));
    }

    private static ClaimsPrincipal OrdinaryPrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "Climber"),
                new Claim("uid", Guid.NewGuid().ToString()),
            ],
            "Cookies"));
    }

    private static string? ExtractCookieValue(HttpContext context, string name)
    {
        foreach (var header in context.Response.Headers.SetCookie)
        {
            if (header is null || !header.StartsWith($"{name}=", StringComparison.Ordinal))
            {
                continue;
            }

            return header[(name.Length + 1)..].Split(';')[0];
        }

        return null;
    }

    private static HttpContext WithCookie(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{KioskDeviceCookie.Name}={value}";
        return context;
    }
}
