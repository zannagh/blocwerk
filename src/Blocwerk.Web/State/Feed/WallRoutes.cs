// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State.Feed;

/// <summary>Which pages belong to a wall, for leaving them when the circuit loses its access.</summary>
public static class WallRoutes
{
    /// <summary>
    /// True when <paramref name="relativePath"/> (base-relative, as <c>NavigationManager.ToBaseRelativePath</c> gives it)
    /// is a page of <paramref name="wallId"/>: <c>walls/{wallId}</c> or anything under it, except the share-link pages,
    /// which read without membership.
    /// </summary>
    public static bool IsMemberPageOf(string relativePath, Guid wallId)
    {
        var path = relativePath.Split('?', '#')[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2
            && string.Equals(segments[0], "walls", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(segments[1], out var id) && id == wallId
            && !segments.Contains("shared", StringComparer.OrdinalIgnoreCase);
    }
}
