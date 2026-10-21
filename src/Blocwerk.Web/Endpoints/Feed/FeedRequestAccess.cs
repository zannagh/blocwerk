// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// Runs <see cref="FeedAccessGate"/> for a request, and answers its refusals: 401 <c>unauthenticated</c>, 403
/// <c>no-access</c>, 410 <c>account-gone</c> (<see cref="FeedRefusals"/>), each with a <see cref="FeedRefusal"/> body and
/// <c>Cache-Control: no-store</c>.
/// </summary>
internal static class FeedRequestAccess
{
    public static FeedCaller CallerOf(HttpContext http) =>
        new(http.User.Identity?.IsAuthenticated == true, http.User.IsApiKeyPrincipal());

    public static FeedSession SessionOf(HttpContext http) => new(
        CallerOf(http),
        http.RequestServices.GetRequiredService<ICurrentUserService>(),
        http.RequestServices.GetService<IKioskContext>(),
        http.RequestServices.GetService<IKioskKeyValidator>());

    public static async Task<FeedAccess> ForWallAsync(HttpContext http, Guid wallId, string? shareToken, CancellationToken ct)
    {
        await using var db = http.RequestServices.GetRequiredService<RootDbContextFactory>().CreateDbContext();
        return await FeedAccessGate.ForWallAsync(db, wallId, shareToken, SessionOf(http), ct);
    }

    public static Task<FeedAccess> ForSelfAsync(HttpContext http) => FeedAccessGate.ForSelfAsync(SessionOf(http));

    public static IResult Refused(HttpContext http, FeedAccessRefusal refusal)
    {
        NoStore(http);
        return refusal switch
        {
            FeedAccessRefusal.Unauthenticated => Refusal(FeedRefusals.Unauthenticated, StatusCodes.Status401Unauthorized),
            FeedAccessRefusal.AccountGone => Refusal(FeedRefusals.AccountGone, StatusCodes.Status410Gone),
            _ => Refusal(FeedRefusals.NoAccess, StatusCodes.Status403Forbidden),
        };
    }

    /// <summary>Feed answers are per viewer and per moment: never stored by the browser or a proxy.</summary>
    public static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    /// <summary>
    /// The body of a 401 the authorization policy answers before a feed handler runs (the cookie challenge), so a
    /// signed-out client reads the same <c>unauthenticated</c> refusal either way.
    /// </summary>
    public static Task WriteChallengeAsync(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.CacheControl = "no-store";
        return response.WriteAsJsonAsync(new FeedRefusal(FeedRefusals.Unauthenticated), FeedJson.Options);
    }

    private static IResult Refusal(string error, int status) =>
        Results.Json(new FeedRefusal(error), FeedJson.Options, statusCode: status);
}
