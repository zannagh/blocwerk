namespace Blocwerk.Web;

/// <summary>
/// Validates and resolves the "last page" value the client records into the
/// blocwerk-last-page cookie, so the homepage redirect can only ever send an
/// authenticated user to a safe, local, in-app path.
/// </summary>
public static class LastPageRedirect
{
    /// <summary>
    /// Returns the stored target when it is a safe local path, otherwise the fallback.
    /// </summary>
    public static string Resolve(string? raw, string fallback)
    {
        return IsSafeTarget(raw) ? raw! : fallback;
    }

    /// <summary>
    /// Only follow a stored value that is a local, absolute path — and never one that would
    /// loop back to "/", bounce the user into an auth/account flow, or resurrect a one-off
    /// share/join URL. Rejects absolute ("://") and protocol-relative ("//"/"/\") URLs so the
    /// cookie can't be used as an open redirect.
    /// </summary>
    public static bool IsSafeTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // Strip query/fragment FIRST, then validate the path only. This also means a "://"
        // hiding in the query string can never fool the scheme check below.
        var cut = raw.IndexOfAny(new[] { '?', '#' });
        var path = cut >= 0 ? raw.Substring(0, cut) : raw;

        if (path.Length == 0 || path[0] != '/')
        {
            return false;
        }

        // Block "//host" (protocol-relative) and "/\host" (a backslash browsers normalise to
        // "//host"), both of which escape to an external origin.
        if (path.Length > 1 && (path[1] == '/' || path[1] == '\\'))
        {
            return false;
        }

        if (path.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (path == "/"
            || IsPathSegment(path, "/account")
            || IsPathSegment(path, "/login")
            || IsPathSegment(path, "/logout")
            || IsPathSegment(path, "/join")
            || IsPathSegment(path, "/home")
            || ContainsSharedSegment(path))
        {
            return false;
        }

        return true;
    }

    private static bool IsPathSegment(string path, string prefix)
    {
        return path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
    }

    // True when any path segment is exactly "shared", e.g. "/walls/1/boulders/2/shared/tok".
    private static bool ContainsSharedSegment(string path)
    {
        return path.Contains("/shared/", StringComparison.Ordinal)
            || path.EndsWith("/shared", StringComparison.Ordinal);
    }
}
