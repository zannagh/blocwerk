// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client;

/// <summary>
/// The client's gym routes and their Server twins. The service worker redirects a failed gym navigation to /v only
/// for a path a declared route covers (plan 2026-10-22 §1, PR 3.9), so a route joins <see cref="Declared"/> in the
/// PR that implements its view, never earlier.
/// </summary>
public static class ClientRoutes
{
    /// <summary>The base path the client is served under, as the shell's base element names it (Blocwerk.Web's OfflineClientShell).</summary>
    public const string BasePath = "/v/";

    /// <summary>The router's not-found page. It is no gym route, so the worker never redirects to it.</summary>
    public const string NotOffline = "not-offline";

    /// <summary>The wall list.</summary>
    public const string WallList = "walls";

    /// <summary>A wall's view.</summary>
    public const string Wall = "walls/{WallId:guid}";

    /// <summary>A wall's view through a share link.</summary>
    public const string SharedWall = "walls/{WallId:guid}/shared/{ShareToken}";

    /// <summary>A boulder's detail.</summary>
    public const string Boulder = "walls/{WallId:guid}/boulders/{BoulderId:guid}";

    /// <summary>A boulder's detail through a share link.</summary>
    public const string SharedBoulder = "walls/{WallId:guid}/boulders/{BoulderId:guid}/shared/{ShareToken}";

    /// <summary>
    /// Gets the route templates (relative to <see cref="BasePath"/>) the client renders from local data: the Server's
    /// templates of the same views, so the worker hands over exactly those (PR 3.11: wall list, wall view, boulder
    /// detail; 3.13 adds gallery and 3D). Each is a page's <c>@page</c> template too (a Core test pins both ways).
    /// </summary>
    public static IReadOnlyList<string> Declared { get; } = [WallList, Wall, SharedWall, Boulder, SharedBoulder];

    /// <summary>The client link of a wall's view, relative to <see cref="BasePath"/> (the router navigates in place).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="shareToken">The share link's token, or null.</param>
    /// <returns>The relative link.</returns>
    public static string WallLink(Guid wallId, string? shareToken) => string.IsNullOrEmpty(shareToken)
        ? $"walls/{wallId}"
        : $"walls/{wallId}/shared/{Uri.EscapeDataString(shareToken)}";

    /// <summary>The client link of a boulder's detail, relative to <see cref="BasePath"/>.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="shareToken">The share link's token, or null.</param>
    /// <returns>The relative link.</returns>
    public static string BoulderLink(Guid wallId, Guid boulderId, string? shareToken) => string.IsNullOrEmpty(shareToken)
        ? $"walls/{wallId}/boulders/{boulderId}"
        : $"walls/{wallId}/boulders/{boulderId}/shared/{Uri.EscapeDataString(shareToken)}";

    /// <summary>
    /// The Server page for a client path given relative to <see cref="BasePath"/>, keeping its query. The not-found page
    /// itself maps to the Server's home.
    /// </summary>
    /// <param name="relativePath">A path as <c>NavigationManager.ToBaseRelativePath</c> returns it.</param>
    /// <returns>A root-relative URL outside /v, which the browser opens with a full navigation.</returns>
    public static string ServerPath(string relativePath)
    {
        var path = relativePath.TrimStart('/', '\\');
        var query = path.IndexOfAny(['?', '#']);
        var bare = query < 0 ? path : path[..query];
        if (bare.Equals(NotOffline, StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return "/" + path;
    }

    /// <summary>The sign-in page, sending the viewer back to <paramref name="serverPath"/> afterwards.</summary>
    /// <param name="serverPath">A root-relative Server path (<see cref="ServerPath"/>).</param>
    /// <returns>/account/login with its <c>returnUrl</c> (the login controller accepts local URLs only).</returns>
    public static string LoginPath(string serverPath) => serverPath == "/"
        ? "/account/login"
        : $"/account/login?returnUrl={Uri.EscapeDataString(serverPath)}";
}
