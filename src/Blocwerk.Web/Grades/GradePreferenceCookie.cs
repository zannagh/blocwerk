// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Grades;

/// <summary>
/// The <c>blocwerk-grade-pref</c> cookie (Phase 3 plan §2, "Grade scale"): the signed-in viewer's preferred grade scale
/// (<see cref="Core.Entities.User.PreferFontGrades"/>), so the offline client, which has no account to ask, can show
/// grades the way the Server pages do. Without it the client shows the wall's own scale.
/// <list type="bullet">
/// <item><description>Set by every Server page response to a signed-in person who is not on a kiosk
/// (<see cref="GradePreferenceCookieMiddleware"/>), so a changed preference reaches it on the next page.</description></item>
/// <item><description>NEVER set for a kiosk session, nobody picked or acting as a member (PR 3.15): a tablet is shared, and
/// a member's preference must not outlive their 30 minutes on it. A kiosk page deletes one a browser had before it was
/// paired, and so does a page with nobody signed in.</description></item>
/// </list>
/// Readable by script (the client reads <c>document.cookie</c>); it holds a display preference, nothing that grants
/// anything.
/// </summary>
public static class GradePreferenceCookie
{
    /// <summary>The cookie's name.</summary>
    public const string Name = "blocwerk-grade-pref";

    /// <summary>The value for Fontainebleau grades.</summary>
    public const string Font = "font";

    /// <summary>The value for V grades.</summary>
    public const string VScale = "v";

    // A preference, renewed by every page, so it lapses about a year after the last visit.
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    /// <summary>The value a page response leaves on the device; null means none (an existing one is deleted).</summary>
    /// <param name="isKiosk">The request is a kiosk session's, whoever it acts as.</param>
    /// <param name="preferFont">The signed-in viewer's preference, or null when nobody is signed in.</param>
    /// <returns><see cref="Font"/>, <see cref="VScale"/>, or null.</returns>
    public static string? ValueFor(bool isKiosk, bool? preferFont) => isKiosk || preferFont is not { } font
        ? null
        : font ? Font : VScale;

    /// <summary>Sets the cookie to <paramref name="value"/>, or deletes one the request carried when it is null.</summary>
    /// <param name="http">The page request.</param>
    /// <param name="value">The value (<see cref="ValueFor"/>).</param>
    public static void Apply(HttpContext http, string? value)
    {
        var current = http.Request.Cookies[Name];
        if (value is null)
        {
            if (current is not null)
            {
                http.Response.Cookies.Delete(Name, Options(http));
            }

            return;
        }

        var options = Options(http);
        options.MaxAge = Lifetime;
        http.Response.Cookies.Append(Name, value, options);
    }

    private static CookieOptions Options(HttpContext http) => new()
    {
        Path = "/",
        HttpOnly = false,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
    };
}
