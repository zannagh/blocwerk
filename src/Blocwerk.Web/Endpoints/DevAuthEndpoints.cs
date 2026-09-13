using System.Security.Claims;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// DEVELOPMENT-ONLY sign-in shortcut for automated (Playwright) end-to-end tests, so a test can act
/// as an existing wall owner WITHOUT real OAuth/password credentials. It issues the exact same auth
/// cookie a normal password login issues (see
/// <c>AccountController.CompletePasswordSignInAsync</c>) — same cookie scheme, same claim shape —
/// so the resulting Blazor Server circuit is authenticated indistinguishably from a real login.
/// <para>
/// This route is mapped ONLY inside the <c>app.Environment.IsDevelopment()</c> block in
/// <c>Program.cs</c>, next to the other dev harness endpoints (<see cref="DevWallUpdateEndpoints"/>).
/// It is NEVER mapped outside Development, and the handler additionally re-checks the environment as
/// defence in depth. It MUST NEVER be enabled in Production: the Development + localhost boundary is
/// the only gate — there is no token or secret guarding it. No new configuration is required.
/// </para>
/// </summary>
internal static class DevAuthEndpoints
{
    public static void MapDevAuth(this IEndpointRouteBuilder endpoints)
    {
        // GET (not POST) on purpose: an automation can drive it with a plain navigation, no
        // anti-forgery token, and the browser keeps the Set-Cookie from the 302 for the next request.
        endpoints.MapGet("/dev/login", LoginAsync);
    }

    // Signs the caller in as an existing user identified by one of, in priority order:
    //   ?userId=<guid>  — that exact user
    //   ?email=<email>  — the user with that (normalized) email
    //   ?wallId=<guid>  — the OWNER of that wall (Wall.OwnerId)
    // then 302-redirects to ?returnUrl (local only) or "/". 404 if the user can't be resolved.
    private static async Task<IResult> LoginAsync(
        HttpContext http,
        IHostEnvironment environment,
        IDbContextFactory<BlocwerkDbContext> factory,
        Guid? userId,
        string? email,
        Guid? wallId,
        string? returnUrl)
    {
        // Defence in depth: even though this route is only mapped under the Development guard, refuse
        // outright if we are somehow not in Development.
        if (!environment.IsDevelopment())
        {
            return Results.NotFound();
        }

        await using var db = await factory.CreateDbContextAsync(http.RequestAborted);

        // Guid.Empty disables the wall membership query filter, so a wall can be resolved regardless
        // of who (if anyone) is currently signed in. Users have no query filter.
        db.CurrentUserId = Guid.Empty;

        var user = await ResolveUserAsync(db, userId, email, wallId, http.RequestAborted);
        if (user is null)
        {
            return Results.NotFound();
        }

        // MIRRORS AccountController.CompletePasswordSignInAsync exactly: the "uid" claim makes
        // CurrentUserService resolve this session by the exact user id (its terminal path 0, which
        // never creates or misresolves a user). The NameIdentifier/Name claims carry the legacy
        // identifier and display name, matching a real password sign-in claim-for-claim.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserAuthId),
            new(ClaimTypes.Name, user.UserName),
            new("Name", user.UserName),
            new("uid", user.Id.ToString()),
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        var authProperties = new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = true,
        };

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, authProperties);

        // Only ever follow a local returnUrl — never bounce to an absolute/cross-site value.
        var target = !string.IsNullOrEmpty(returnUrl)
            && Uri.TryCreate(returnUrl, UriKind.Relative, out _)
                ? returnUrl
                : "/";

        return Results.Redirect(target);
    }

    private static async Task<User?> ResolveUserAsync(
        BlocwerkDbContext db,
        Guid? userId,
        string? email,
        Guid? wallId,
        CancellationToken cancellationToken)
    {
        if (userId is { } id)
        {
            return await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            // Email is stored normalized (trimmed, lower-cased); match it the same way.
            var normalized = email.Trim().ToLowerInvariant();
            return await db.Users.FirstOrDefaultAsync(u => u.Email == normalized, cancellationToken);
        }

        if (wallId is { } wid)
        {
            var ownerId = await db.Walls
                .Where(w => w.Id == wid)
                .Select(w => w.OwnerId)
                .FirstOrDefaultAsync(cancellationToken);

            if (ownerId != Guid.Empty)
            {
                return await db.Users.FirstOrDefaultAsync(u => u.Id == ownerId, cancellationToken);
            }
        }

        return null;
    }
}
