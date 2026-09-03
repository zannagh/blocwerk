using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Middleware;

public class DevAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _name;
    private readonly string _id;

    public DevAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;

        // BLOCWERK_DEV_USER is the full User.Identifier to act as, which is "{name}__{id}" (see
        // ClaimsHelper.ToUserIdentifier). Split on the FIRST "__" so the reconstructed claims yield
        // exactly that identifier — set it to your real value and you own your cloned wall.
        var identifier = Environment.GetEnvironmentVariable("BLOCWERK_DEV_USER") ?? "Dev Admin__dev-admin";
        var sep = identifier.IndexOf("__", StringComparison.Ordinal);
        if (sep > 0)
        {
            _name = identifier[..sep];
            _id = identifier[(sep + 2)..];
        }
        else
        {
            _name = identifier;
            _id = identifier;
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true
            && !context.Request.Path.StartsWithSegments("/account")
            && !context.Request.Path.StartsWithSegments("/_framework")
            && !context.Request.Path.StartsWithSegments("/css")
            && !context.Request.Path.StartsWithSegments("/js"))
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, _id),
                new(ClaimTypes.Name, _name),
                new("Name", _name),

                // Dev login re-authenticates on the spot every time it runs, so it is always fresh —
                // and without the stamp the very first dev request could not create its own user.
                Services.AuthFreshness.Stamp(),
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal);

            context.User = principal;
        }

        await _next(context);
    }
}
