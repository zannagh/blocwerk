using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Kiosk;

/// <summary>
/// What a kiosk session may reach, regardless of the acting user's authority over the wall.
/// </summary>
/// <remarks>
/// This is a second axis, not a cap on the first. The acting user keeps FULL authority over the wall
/// and its boulders — including destructive wall-admin actions — because that is a locked product
/// decision. What is blocked here is everything whose blast radius escapes the tablet and the
/// session: taking over the account (password, second factor, e-mail, linking another login),
/// minting a credential or a membership that outlives the 30 minutes, creating accounts or walls,
/// and app-wide administration, which is not wall authority at all.
/// <para>
/// <b>This is an ALLOW-list, deliberately.</b> It started as a deny-list and that shape was wrong for
/// a device bolted to a public wall: every page added to the app afterwards is reachable from the
/// tablet until somebody remembers to add it here, and the list only ever grows. Inverted, the
/// default for anything new is "not from the tablet", and the cost of forgetting is a page that has
/// to be allowed rather than a hole that has to be found. <see cref="AllowedPageTypes"/> is kept
/// honest by a test that fails when ANY routable page in Blocwerk.Web is neither allowed nor
/// explicitly refused, so adding a page forces the decision rather than defaulting either way.
/// </para>
/// <para>
/// <see cref="DeniedPaths"/> is still needed alongside it, for the dangerous routes that sit UNDER an
/// allowed prefix — <c>/walls/create</c> under <c>/walls</c>. It is consulted first and wins.
/// </para>
/// <para>
/// Three enforcement points share this list, because no single one covers everything:
/// <see cref="Middleware.KioskRestrictionMiddleware"/> for anything that arrives as an HTTP request,
/// <see cref="Authorization.KioskRouteHandler"/> for in-circuit Blazor navigation (which never
/// touches middleware), and the service guards in <c>KioskGuardedApiKeyService</c>,
/// <c>CurrentUserService</c>, <c>KioskService</c>, <c>PasswordLoginService</c>, <c>WallService</c>
/// and <c>WallAdminGuard</c> for mutations invoked directly from inside a circuit. The service
/// guards are the real gate; the two route gates keep users out of pages that would only fail later.
/// </para>
/// </remarks>
public static class KioskRestrictions
{
    /// <summary>
    /// Paths refused outright, even when they sit under something in
    /// <see cref="AllowedPathPrefixes"/>. Matched by path SEGMENT prefix, so
    /// <c>/settings/api-keys/anything</c> is covered and <c>/settings/api-keys-ish</c> is not.
    /// </summary>
    public static readonly IReadOnlyList<string> DeniedPaths =
    [
        // Account security. The password/TOTP pages live under /profile, and there is no separate
        // endpoint for the e-mail change — it is written inline by the profile page — so the whole
        // profile surface is off limits from a tablet.
        "/profile",

        // Attaching another OAuth identity to the acting user's account: a permanent takeover that
        // would outlive the session by years. /account/link starts the flow and /account/external
        // carries it to the provider, so both are refused.
        //
        // /account/callback is deliberately NOT here. That one route serves TWO flows — it branches
        // into link/merge only when the link-intent cookie is present, and is otherwise the ordinary
        // OAuth sign-in completion. Refusing the path refused both, which made every allowed sign-in
        // surface (/oauth-select, /signing-in, /account/login, /oauth-callback) a dead end and left a
        // wall admin unable to sign in at the tablet at all. The link/merge half is refused inside
        // the branch instead, in AccountController.HandleLinkCallbackAsync.
        "/account/link",
        "/account/external",

        // Setting a password from the tablet, by either route: the guarded one and the reset one.
        "/account/password",
        "/forgot-password",
        "/reset-password",

        // Creating an ACCOUNT from the tablet. Paired with a share link generated in the same
        // session, this is how a stranger turns thirty minutes of borrowed authority into permanent
        // membership; the share link is refused at the service too.
        "/signup",

        // Redeeming a share link — the other half of that pair.
        "/join",

        // Creating a wall the tablet is not, and can never be, registered to.
        "/walls/create",

        // API-key minting pages.
        "/settings/api-keys",

        // App-wide administration. Note this is NOT wall authority — a kiosk session keeps all of
        // that — it is authority over every wall and every user in the installation.
        "/administration",
    ];

    /// <summary>
    /// Everything a kiosk tablet legitimately needs, by path SEGMENT prefix. Anything not under one
    /// of these (and not the site root) is refused.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedPathPrefixes =
    [
        // The wall, its boulders, and the wall list — which a kiosk sees narrowed to its own wall
        // anyway, by the query filter. /walls/create is carved back out above.
        "/walls",

        // The kiosk's own endpoints: registering the tablet, picking a member, releasing, and the
        // member picker page.
        "/kiosk",

        // The acting user's own climbing record and training logs: reads and writes that never
        // leave their account, which is exactly what the tablet is for.
        "/activity",
        "/home",
        "/training",

        // Calculators and guides. No account surface at all, and genuinely useful at the wall.
        "/tools",
        "/guides",

        // Static content pages.
        "/about",
        "/privacy",

        // Signing in and out on the tablet. The sign-in surface must stay reachable: kiosk
        // registration itself posts from /oauth-select, and its failure redirect lands back there.
        // The account-TAKEOVER halves of this surface (/account/link, /account/external,
        // /account/password) are refused above.
        "/oauth-select",
        "/oauth-callback",
        "/signing-in",
        "/account/login",

        // Where an OAuth sign-in actually completes. Allowed because refusing it refuses ordinary
        // sign-in too; its link/merge branch refuses itself for a kiosk session.
        "/account/callback",
        "/account/logout",
        "/account/totp",
        "/authorize",
        "/token",

        // The REST and media surfaces. These carry their own authorisation, and the wall query
        // filter pins the wall-scoped ones to the kiosk's wall; the offline replay controllers under
        // /api/offline are what the tablet uses to flush a queue after a network drop.
        "/api",
        "/media",

        // Blazor's own plumbing: the circuit hub, the runtime, and Razor class library assets.
        "/_blazor",
        "/_framework",
        "/_content",

        // Static assets from wwwroot, plus the operational endpoints that never carry identity.
        "/css",
        "/js",
        "/icons",
        "/manifest.webmanifest",
        "/robots.txt",
        "/offline.html",
        "/favicon.ico",
        "/health",
        "/metrics",
    ];

    /// <summary>
    /// Blazor page component type names a kiosk session may route to in-circuit. Matched by full
    /// type name because this project cannot reference Blocwerk.Web (the dependency runs the other
    /// way); <c>KioskAuthTests</c> has the test that keeps the two in step.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedPageTypes =
    [
        "Blocwerk.Web.Components.Pages.Home",
        "Blocwerk.Web.Components.Pages.About",
        "Blocwerk.Web.Components.Pages.Privacy",
        "Blocwerk.Web.Components.Pages.Activity",
        "Blocwerk.Web.Components.Pages.ActivityView",
        "Blocwerk.Web.Components.Pages.HomeWall",
        "Blocwerk.Web.Components.Pages.OAuthSelect",
        "Blocwerk.Web.Components.Pages.SigningIn",
        "Blocwerk.Web.Components.Pages.TotpChallenge",
        "Blocwerk.Web.Components.Pages.Guides.Guides",
        "Blocwerk.Web.Components.Pages.Guides.AngleWedge",
        "Blocwerk.Web.Components.Pages.Guides.HomewallsVolumes",
        "Blocwerk.Web.Components.Pages.Kiosk.KioskUsers",
        "Blocwerk.Web.Components.Pages.Tools.ImageStitcher",
        "Blocwerk.Web.Components.Pages.Training.Hangboard",
        "Blocwerk.Web.Components.Pages.Training.Pullups",
        "Blocwerk.Web.Components.Pages.Walls.WallList",
        "Blocwerk.Web.Components.Pages.Walls.WallDetail",
        "Blocwerk.Web.Components.Pages.Walls.BoulderCreate",
        "Blocwerk.Web.Components.Pages.Walls.BoulderDetail",
        "Blocwerk.Web.Components.Pages.Walls.BoulderRevise",
    ];

    /// <summary>
    /// Routable pages a kiosk session is explicitly refused. Everything routable that is on neither
    /// this list nor <see cref="AllowedPageTypes"/> is ALSO refused — this list exists so the test
    /// that enumerates Blocwerk.Web's pages can tell "decided: no" from "nobody has looked at it
    /// yet", and so the reason for each refusal has somewhere to live.
    /// </summary>
    public static readonly IReadOnlyList<string> RefusedPageTypes =
    [
        // Account takeover, in one page: password, TOTP, e-mail, display name.
        "Blocwerk.Web.Components.Pages.Profile",

        // Credentials that outlive the session.
        "Blocwerk.Web.Components.Pages.Settings.ApiKeys",

        // Authority over every wall in the installation.
        "Blocwerk.Web.Components.Pages.Administration.Dashboard",

        // Creating an account, and resetting the password of an existing one, from a public tablet.
        "Blocwerk.Web.Components.Pages.Signup",
        "Blocwerk.Web.Components.Pages.ForgotPassword",
        "Blocwerk.Web.Components.Pages.ResetPassword",

        // Permanent membership: minted on WallDetail, redeemed here. Both halves are refused, and
        // the minting is refused at the service as well.
        "Blocwerk.Web.Components.Pages.Join",

        // A wall the tablet is not registered to, and cannot be.
        "Blocwerk.Web.Components.Pages.Walls.WallCreate",
    ];

    /// <summary>
    /// True when this session must be refused an account link/merge.
    /// </summary>
    /// <remarks>
    /// The link/merge branch of <c>/account/callback</c> calls this instead of the path being listed
    /// in <see cref="DeniedPaths"/>. The path cannot be denied: ORDINARY OAuth sign-in completes on
    /// the very same route, and denying it left a wall admin unable to sign in at a registered
    /// tablet at all. The branch is the only place the two flows are actually distinguishable.
    /// </remarks>
    public static bool IsBlockedAccountLink(IKioskContext? kioskContext)
    {
        return kioskContext is { IsKiosk: true };
    }

    /// <summary>True when a kiosk session must be refused this request path.</summary>
    public static bool IsBlockedPath(PathString path)
    {
        // The site root is the app's own landing page and every redirect's safe harbour. Matching it
        // as a prefix would allow everything, so it is an exact match instead.
        if (!path.HasValue || path.Value is "/" or "")
        {
            return false;
        }

        if (MatchesAny(path, DeniedPaths))
        {
            return true;
        }

        return !MatchesAny(path, AllowedPathPrefixes);
    }

    /// <summary>True when a kiosk session must be refused this page component.</summary>
    /// <remarks>
    /// A null type is NOT a refusal: the route gates evaluate the default policy for HTTP endpoints
    /// too, where there is no page at all, and failing there would break every <c>[Authorize]</c>
    /// endpoint in the app.
    /// </remarks>
    public static bool IsBlockedPageType(Type? pageType)
    {
        if (pageType?.FullName is not { } name)
        {
            return false;
        }

        return !AllowedPageTypes.Contains(name);
    }

    private static bool MatchesAny(PathString path, IReadOnlyList<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
