using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Builds <see cref="BlocwerkPolicies.HumanOrUserApiKey"/>: the browser user's own policy, widened by
/// exactly one principal — a personal API key acting as its owner.
/// </summary>
/// <remarks>
/// Names no schemes, for the reason <see cref="AuthenticationServices.BuildHumanPolicy"/> gives: the
/// default policy scheme already forwards a <c>bwk_</c> bearer to the API-key handler on an
/// <see cref="ApiKeySurface"/> path and everything else to the cookie. A wall key stays out because it
/// lives on a device bolted to a wall and must be assumed to leak; a kiosk key belongs to a tablet;
/// an installation key only records the admin who minted it and must never inherit their access.
/// </remarks>
public static class HumanOrPersonalApiKeyPolicy
{
    public static AuthorizationPolicy Build(AuthorizationPolicyBuilder policy)
    {
        return policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => Admits(context.User))
            .AddRequirements(new KioskRouteRequirement())
            .Build();
    }

    /// <summary>
    /// True for any non-API-key principal, and for a personal key (no wall) its owner allowed to change
    /// walls (<c>ApiKey.AllowWrite</c>).
    /// </summary>
    public static bool Admits(ClaimsPrincipal user)
    {
        return !user.IsApiKeyPrincipal() || user.IsWritablePersonalKey();
    }
}
