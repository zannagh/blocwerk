using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Second layer of the CSRF defence on the cookie-authenticated offline endpoints.
/// <para>
/// The first layer is the real one: every offline POST carries <c>[ValidateAntiForgeryToken]</c>,
/// and the queue fetches a fresh request token from <c>GET /api/offline/antiforgery</c> per flush
/// rather than holding one across the offline period (see offline-transport.js). This attribute
/// stays because it costs nothing and closes the gap on its own: a cross-site <c>&lt;form&gt;</c>
/// cannot set request headers at all, and a cross-origin <c>fetch</c> that sets one triggers a CORS
/// preflight, which fails because the app registers no CORS policy. Together with the auth cookie's
/// <c>SameSite=Lax</c> (pinned explicitly in <see cref="Program"/>), which stops the cookie riding
/// along on a cross-site POST in the first place, that is three independent barriers.
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
