using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// CSRF defence for the cookie-authenticated offline endpoints.
/// <para>
/// These endpoints take JSON, not form data, so the synchroniser-token pattern would force the
/// client queue to hold a server-issued token across an arbitrarily long offline period — a token
/// that may well have expired by the time the queue replays. Instead we require a custom request
/// header. A cross-site <c>&lt;form&gt;</c> cannot set request headers at all, and a cross-origin
/// <c>fetch</c> that sets one triggers a CORS preflight, which fails because the app registers no
/// CORS policy. Combined with the auth cookie's <c>SameSite=Lax</c> (pinned explicitly in
/// <see cref="Program"/>), which stops the cookie riding along on any cross-site POST in the first
/// place, this closes the forgery path without any expiring state on the client.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireClientHeaderAttribute : ActionFilterAttribute
{
    /// <summary>
    /// The header the client queue must send. The value is irrelevant; only a same-origin
    /// caller is able to set the header at all.
    /// </summary>
    public const string HeaderName = "X-Blocwerk-Client";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.HttpContext.Request.Headers.ContainsKey(HeaderName))
        {
            context.Result = new ObjectResult(new OfflineActionError(
                $"Missing required '{HeaderName}' header.", true))
            {
                StatusCode = StatusCodes.Status400BadRequest,
            };
            return;
        }

        base.OnActionExecuting(context);
    }
}
