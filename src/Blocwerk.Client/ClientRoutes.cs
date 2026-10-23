// <copyright file="ClientRoutes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Client;

/// <summary>
/// The client's gym routes and their Server twins. The service worker redirects a failed gym navigation to /v only
/// for a path a declared route covers (plan 2026-10-22 §1, PR 3.9), so a route joins <see cref="Declared"/> in the
/// PR that implements its view, never earlier.
/// </summary>
public static class ClientRoutes
{
    /// <summary>The base path the client is served under, as its index.html's base element names it.</summary>
    public const string BasePath = "/v/";

    /// <summary>The router's not-found page. It is no gym route, so the worker never redirects to it.</summary>
    public const string NotOffline = "not-offline";

    /// <summary>
    /// Gets the route templates (relative to <see cref="BasePath"/>) the client renders from local data. None yet: the
    /// skeleton (PR 3.8) declares no route, and the views add theirs (wall list, wall view and boulder detail in 3.11,
    /// gallery and 3D in 3.13).
    /// </summary>
    public static IReadOnlyList<string> Declared { get; } = [];

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
}
