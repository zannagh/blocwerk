using System.Security.Claims;
using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Kiosk;

/// <summary>
/// Scoped <see cref="IKioskContext"/>: one instance per HTTP request and one per Blazor circuit.
/// </summary>
/// <remarks>
/// <para><b>The null-HttpContext hazard.</b> <see cref="IHttpContextAccessor"/> is only dependable
/// while an HTTP request is on the stack. A kiosk context that read <c>HttpContext.Request.Cookies</c>
/// on every access would answer correctly on the first paint and then quietly answer "not a kiosk"
/// for the rest of the circuit — which, for a restriction, means silently unlocking it. Two things
/// prevent that here:</para>
/// <list type="number">
/// <item><description>The PRIMARY source is the acting session's kiosk CLAIMS, read through
/// <see cref="AuthenticationStateProvider"/> — the very same object <c>CurrentUserService</c> resolves
/// the acting user from. So kiosk scoping is available exactly wherever identity is: if the principal
/// cannot be read, there is no acting user either, and the session has nothing to restrict.</description></item>
/// <item><description>Resolution happens ONCE and is cached for the lifetime of the scope, and it is
/// driven eagerly at the two moments an HTTP context is guaranteed — the kiosk middleware on every
/// request, and <c>KioskCircuitHandler.OnCircuitOpenedAsync</c> at circuit start. The device cookie
/// is therefore captured while it is readable and then held in circuit-scoped state.</description></item>
/// </list>
/// <para>The synchronous members below fall back to resolving on demand rather than reporting a
/// wrong answer, so there is no window in which an unresolved context reads as "not a kiosk".</para>
/// </remarks>
public sealed class KioskContext : IKioskContext
{
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly AuthenticationStateProvider? authenticationStateProvider;
    private readonly KioskDeviceCookie deviceCookie;

    private bool initialized;
    private bool isKiosk;
    private Guid? wallId;
    private Guid? apiKeyId;

    public KioskContext(
        IHttpContextAccessor httpContextAccessor,
        KioskDeviceCookie deviceCookie,
        AuthenticationStateProvider? authenticationStateProvider = null)
    {
        this.httpContextAccessor = httpContextAccessor;
        this.deviceCookie = deviceCookie;
        this.authenticationStateProvider = authenticationStateProvider;
    }

    public bool IsKiosk
    {
        get
        {
            EnsureInitialized();
            return isKiosk;
        }
    }

    public Guid? KioskWallId
    {
        get
        {
            EnsureInitialized();
            return wallId;
        }
    }

    public Guid? KioskApiKeyId
    {
        get
        {
            EnsureInitialized();
            return apiKeyId;
        }
    }

    public async Task InitializeAsync()
    {
        if (initialized)
        {
            return;
        }

        var principal = await ResolvePrincipalAsync();
        Apply(principal, deviceCookie.Read(httpContextAccessor.HttpContext));
    }

    /// <summary>
    /// Combines the two sources into the final answer. Separated out so it can be exercised
    /// directly, without an HTTP context or an authentication state provider.
    /// </summary>
    internal void Apply(ClaimsPrincipal? principal, KioskDeviceRegistration? registration)
    {
        initialized = true;

        var claimWallId = principal.IsKioskPrincipal() ? principal.ReadGuid(KioskClaims.WallId) : null;
        var claimKeyId = principal.IsKioskPrincipal() ? principal.ReadGuid(KioskClaims.KeyId) : null;

        if (claimWallId is null && registration is null)
        {
            isKiosk = false;
            wallId = null;
            apiKeyId = null;
            return;
        }

        // Either source alone is enough to call this a kiosk. In particular a device that carries the
        // registration cookie stays capped even when somebody signs in on it the ordinary way — it is
        // still a tablet in a public gym.
        isKiosk = true;

        if (claimWallId is not null && registration is not null && claimWallId != registration.WallId)
        {
            // The device was re-registered to a different wall while an acting-as session was live.
            // Rather than pick a winner, resolve to a wall that matches nothing: the session is
            // capped to nothing until it is signed out and picked again.
            wallId = Guid.Empty;
            apiKeyId = claimKeyId ?? registration.ApiKeyId;
            return;
        }

        wallId = claimWallId ?? registration?.WallId;
        apiKeyId = claimKeyId ?? registration?.ApiKeyId;
    }

    private async Task<ClaimsPrincipal?> ResolvePrincipalAsync()
    {
        if (authenticationStateProvider is not null)
        {
            var state = await authenticationStateProvider.GetAuthenticationStateAsync();
            if (state.User.Identity?.IsAuthenticated == true)
            {
                return state.User;
            }
        }

        return httpContextAccessor.HttpContext?.User;
    }

    private void EnsureInitialized()
    {
        if (initialized)
        {
            return;
        }

        // Both sources complete synchronously in this app — CookieAuthenticationStateProvider returns
        // a completed task, and reading a cookie does no I/O — so this never actually blocks. It is a
        // backstop for a caller that reads the context before the middleware or the circuit handler
        // had a chance to prime it; answering "not a kiosk" there would be an unlocked restriction.
        //
        // WARNING — LOAD-BEARING INVARIANT. The blocking wait is safe ONLY because the registered
        // AuthenticationStateProvider (CookieAuthenticationStateProvider) returns an ALREADY-COMPLETED
        // Task from GetAuthenticationStateAsync. This runs on the hot path — MainLayout reads IsKiosk
        // on every render, KioskRouteHandler on every authorization — so a provider that ever returns
        // a genuinely asynchronous task would deadlock every circuit on the Blazor renderer's
        // synchronisation context, silently and everywhere at once. If that provider is ever replaced,
        // this method must become async (and every caller of the synchronous members with it) rather
        // than keeping GetAwaiter().GetResult().
        InitializeAsync().GetAwaiter().GetResult();
    }
}
