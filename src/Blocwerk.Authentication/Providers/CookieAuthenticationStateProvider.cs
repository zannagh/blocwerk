using System.Security.Claims;
using System.Text;
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

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        if (_isInitialized && _cachedState != null)
        {
            return Task.FromResult(_cachedState);
        }

        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext?.User.Identity?.IsAuthenticated == true)
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
