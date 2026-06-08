using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Authentication.Middleware;

public class DevAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public DevAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
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
                new(ClaimTypes.NameIdentifier, "dev-admin__000000000"),
                new(ClaimTypes.Name, "Dev Admin"),
                new("Name", "Dev Admin"),
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
