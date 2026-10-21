// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace Blocwerk.Core.Tests;

/// <summary>Requests for the byte routes built from the urls the app hands out, query string included.</summary>
internal static class AssetRequests
{
    /// <summary>A request carrying <paramref name="url"/>'s query, as a browser following that url would send it.</summary>
    public static DefaultHttpContext For(string url)
    {
        var http = new DefaultHttpContext();
        var at = url.IndexOf('?');
        if (at >= 0)
        {
            http.Request.QueryString = new QueryString(url[at..]);
        }

        return http;
    }

    /// <summary>The value of <paramref name="name"/> in <paramref name="url"/>'s query, or null.</summary>
    public static string? Query(string url, string name)
    {
        var at = url.IndexOf('?');
        if (at < 0)
        {
            return null;
        }

        return QueryHelpers.ParseQuery(url[at..]).TryGetValue(name, out var value) ? value.ToString() : null;
    }
}
