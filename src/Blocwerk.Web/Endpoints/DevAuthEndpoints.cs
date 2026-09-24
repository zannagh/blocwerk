using Blocwerk.Authentication.Services;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// DEVELOPMENT-ONLY sign-in shortcut for automated (Playwright) end-to-end tests, so a test can act
/// as an existing wall owner WITHOUT real OAuth/password credentials. It issues the exact same auth
/// cookie a normal password login issues (both go through <see cref="UserCookieSignIn"/>) — same
/// cookie scheme, same claim shape — so the resulting Blazor Server circuit is authenticated
/// indistinguishably from a real login.
/// <para>
/// This route is mapped ONLY inside the <c>app.Environment.IsDevelopment()</c> block in
/// <c>Program.cs</c>, next to the other dev harness endpoints (<see cref="DevWallUpdateEndpoints"/>).
/// It is NEVER mapped outside Development, and the handler additionally re-checks the environment as
/// defence in depth. It MUST NEVER be enabled in Production: the Development + localhost boundary is
/// the only gate for the id/email/wall lookups — there is no token or secret guarding those. The
/// production-safe way to do the same thing is <c>POST /account/api-key-login</c> (see
/// <c>ApiKeyLoginEndpoints</c>), whose Bearer-key path this route also accepts.
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
    //   Authorization: Bearer bwk_… — the owner of that PERSONAL API key, validated exactly as
    //                    /account/api-key-login validates it (401 when the key does not qualify)
    //   ?userId=<guid>  — that exact user
    //   ?email=<email>  — the user with that (normalized) email
    //   ?wallId=<guid>  — the OWNER of that wall (Wall.OwnerId)
    // then 302-redirects to ?returnUrl (local only) or "/". 404 if the user can't be resolved.
    private static async Task<IResult> LoginAsync(
        HttpContext http,
        IHostEnvironment environment,
        IDbContextFactory<BlocwerkDbContext> factory,
        ApiKeyLoginValidator apiKeyLoginValidator,
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

        // Only ever follow a local returnUrl. The framework check refuses "//host" and "/\host",
        // which the previous "is it a relative URI" test let through as an open redirect.
        var target = LocalReturnUrl.IsLocal(http, returnUrl) ? returnUrl! : "/";

        if (ApiKeyLoginValidator.ReadBearerApiKey(http.Request) is not null)
        {
            // Exactly the production key login: same checks, same marked, key-bounded session.
            var result = await apiKeyLoginValidator.ValidateAsync(http.Request, http.RequestAborted);
            if (!result.Succeeded)
            {
                return Results.Unauthorized();
            }

            await ApiKeySessionSignIn.SignInAsync(http, result.User!, result.Key!);
            return Results.Redirect(target);
        }

        var user = await ResolveUserAsync(factory, userId, email, wallId, http.RequestAborted);
        if (user is null)
        {
            return Results.NotFound();
        }

        // Persistent so a local dev session survives closing the browser (Development only).
        var authProperties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30),
            AllowRefresh = true,
        };

        await UserCookieSignIn.SignInAsync(http, user, authProperties);
        return Results.Redirect(target);
    }

    private static async Task<User?> ResolveUserAsync(
        IDbContextFactory<BlocwerkDbContext> factory,
        Guid? userId,
        string? email,
        Guid? wallId,
        CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Guid.Empty disables the wall membership query filter, so a wall can be resolved regardless
        // of who (if anyone) is currently signed in. Users have no query filter; erased accounts'
        // tombstones are skipped so the dev login cannot sign anybody in as one.
        db.CurrentUserId = Guid.Empty;
        var users = db.Users.Where(u => u.DeletedAt == null);

        if (userId is { } id)
        {
            return await users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            // Email is stored normalized (trimmed, lower-cased); match it the same way.
            var normalized = email.Trim().ToLowerInvariant();
            return await users.FirstOrDefaultAsync(u => u.Email == normalized, cancellationToken);
        }

        if (wallId is { } wid)
        {
            var ownerId = await db.Walls
                .Where(w => w.Id == wid)
                .Select(w => w.OwnerId)
                .FirstOrDefaultAsync(cancellationToken);

            if (ownerId != Guid.Empty)
            {
                return await users.FirstOrDefaultAsync(u => u.Id == ownerId, cancellationToken);
            }
        }

        return null;
    }
}
