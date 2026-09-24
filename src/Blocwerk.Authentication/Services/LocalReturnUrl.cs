using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The local-URL check the MVC sign-in actions use (<c>Url.IsLocalUrl</c>), for the minimal-API
/// sign-in endpoints that have no controller <c>Url</c> helper of their own.
/// </summary>
/// <remarks>
/// Deliberately the framework's own implementation rather than a hand-rolled one: it rejects the
/// protocol-relative (<c>//evil.example</c>) and backslash (<c>/\evil.example</c>) forms that a
/// naive "is it a relative URI" test waves through as an open redirect.
/// </remarks>
public static class LocalReturnUrl
{
    public static bool IsLocal(HttpContext http, string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        var urlHelper = new UrlHelper(new ActionContext(http, http.GetRouteData(), new ActionDescriptor()));
        return urlHelper.IsLocalUrl(url);
    }
}
