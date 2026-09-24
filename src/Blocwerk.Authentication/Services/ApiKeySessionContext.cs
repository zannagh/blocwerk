using System.Security.Claims;
using Blocwerk.Core.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Scoped <see cref="IApiKeySessionContext"/>: reads the acting session's claims, from the Blazor
/// authentication state inside a circuit and from the HTTP request otherwise.
/// </summary>
/// <remarks>
/// The authentication state is read synchronously for the same reason, and under the same
/// load-bearing invariant, as <c>KioskContext.EnsureInitialized</c>: the registered
/// <c>CookieAuthenticationStateProvider</c> always returns a completed task. The answer is not cached,
/// so a session that changes within a scope is never judged by a stale principal.
/// </remarks>
public sealed class ApiKeySessionContext : IApiKeySessionContext
{
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly AuthenticationStateProvider? authenticationStateProvider;

    public ApiKeySessionContext(
        IHttpContextAccessor httpContextAccessor,
        AuthenticationStateProvider? authenticationStateProvider = null)
    {
        this.httpContextAccessor = httpContextAccessor;
        this.authenticationStateProvider = authenticationStateProvider;
    }

    public bool IsApiKeySession =>
        ApiKeySessionClaims.IsApiKeySession(httpContextAccessor.HttpContext?.User)
        || ApiKeySessionClaims.IsApiKeySession(CircuitPrincipal());

    private ClaimsPrincipal? CircuitPrincipal()
    {
        if (authenticationStateProvider is null)
        {
            return null;
        }

        var state = authenticationStateProvider.GetAuthenticationStateAsync();
        return state.IsCompletedSuccessfully ? state.Result.User : state.GetAwaiter().GetResult().User;
    }
}
