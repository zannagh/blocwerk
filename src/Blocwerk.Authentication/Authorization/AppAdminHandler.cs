using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Authorization;

namespace Blocwerk.Authentication.Authorization;

/// <summary>
/// Decides <see cref="AppAdminRequirement"/>. The app-wide Admin role lives on the user row and is
/// never carried on the cookie principal as a claim, so this resolves the current user through
/// <see cref="ICurrentUserService"/> and succeeds only when their role is
/// <see cref="IdentityRole.Admin"/>. An anonymous or non-admin caller simply does not succeed, which
/// leaves the default challenge (a redirect to the login page) to run.
/// </summary>
public sealed class AppAdminHandler : AuthorizationHandler<AppAdminRequirement>
{
    private readonly ICurrentUserService currentUserService;

    public AppAdminHandler(ICurrentUserService currentUserService)
    {
        this.currentUserService = currentUserService;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AppAdminRequirement requirement)
    {
        // An API-key principal must never reach the admin area.
        if (context.User.IsApiKeyPrincipal())
        {
            return;
        }

        try
        {
            var user = await currentUserService.GetCurrentUserAsync();
            if (user?.Role == IdentityRole.Admin)
            {
                context.Succeed(requirement);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Anonymous / unresolved caller: leave the requirement unmet so the default challenge runs.
        }
    }
}
