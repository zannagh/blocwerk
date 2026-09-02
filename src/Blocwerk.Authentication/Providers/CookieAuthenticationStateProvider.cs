using System.Security.Claims;
using System.Text;
using Blocwerk.Authentication.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace Blocwerk.Authentication.Providers;

public class CookieAuthenticationStateProvider : AuthenticationStateProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDataProtectionProvider _dataProtectionProvider;
    private AuthenticationState? _cachedState;
    private bool _isInitialized;

    public CookieAuthenticationStateProvider(IHttpContextAccessor httpContextAccessor, IDataProtectionProvider dataProtectionProvider)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Adopts a principal that was signed in during the CURRENT request, so the rest of that request
    /// resolves as the new identity.
    /// </summary>
    /// <remarks>
    /// <c>SignInAsync</c> only writes a Set-Cookie header; it does not retroactively change
    /// <c>HttpContext.User</c>, and this provider resolves from the request it can see — the
    /// unauthenticated one that arrived. So without this call the rest of the request keeps
    /// resolving as whoever it was before the sign-in (anonymous, or the previous member), and a
    /// pending attempt would be attributed accordingly.
    /// <para>
    /// This makes the new principal explicit for the remainder of the request, and marks it as
    /// settled so no later resolve re-reads the stale request state. It is a within-request fix
    /// only: the next request carries the cookie and resolves normally. Only ever call it with a
    /// principal that has just been signed in on this same request.
    /// </para>
    /// </remarks>
    public void AdoptSignedInPrincipal(ClaimsPrincipal principal)
    {
        _cachedState = new AuthenticationState(principal);
        _isInitialized = true;
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_isInitialized && _cachedState != null)
        {
            return Task.FromResult(_cachedState);
        }

        var httpContext = _httpContextAccessor.HttpContext;

        // An API key is a machine credential that lives on a device bolted to a wall. It may never
        // establish a Blazor circuit: doing so would render every [Authorize] page as its owner.
        // The scheme selection already keeps API keys off page paths; this is the second lock.
        if (httpContext?.User.Identity?.IsAuthenticated == true && !httpContext.User.IsApiKeyPrincipal())
        {
            _cachedState = new AuthenticationState(httpContext.User);
            _isInitialized = true;
            return Task.FromResult(_cachedState);
        }

        string cookieName = $".AspNetCore.{CookieAuthenticationDefaults.AuthenticationScheme}";
        if (httpContext?.Request.Cookies.TryGetValue(cookieName, out string? cookieValue) == true
            && !string.IsNullOrEmpty(cookieValue))
        {
            try
            {
                var principal = ParseCookieTicket(cookieValue);
                if (principal?.Identity?.IsAuthenticated == true)
                {
                    _cachedState = new AuthenticationState(principal);
                    return Task.FromResult(_cachedState);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Cookie Authentication] Failed to parse cookie");
            }
        }

        _cachedState = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        return Task.FromResult(_cachedState);
    }

    private ClaimsPrincipal? ParseCookieTicket(string cookieValue)
    {
        try
        {
            var dataProtector = _dataProtectionProvider.CreateProtector(
                "Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationMiddleware",
                CookieAuthenticationDefaults.AuthenticationScheme,
                "v2");

            string ticketData = dataProtector.Unprotect(cookieValue);
            var ticket = TicketSerializer.Default.Deserialize(Encoding.UTF8.GetBytes(ticketData));

            if (ticket?.Principal != null &&
                ticket.Properties.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                return ticket.Principal;
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "[Cookie Authentication] Failed to parse cookie, it may have expired or been tampered with");
        }

        return null;
    }
}
