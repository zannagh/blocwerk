// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Text.Encodings.Web;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The markup of <see cref="SiteDataClearing.SignedOutPage"/>: a bare page that tells the page-side caches the identity
/// changed and moves on at once. It loads only <c>deploy-drafts.js</c>, whose <c>bw:identity-changed</c> listener drops
/// the editor copies kept on the device for the identity that left. Its target is always a local path; anything else
/// becomes "/".
/// </summary>
internal static class SignedOutMarkup
{
    /// <summary>The event the page-side caches listen for (the asset ledger, the deploy drafts).</summary>
    public const string IdentityChangedEvent = "bw:identity-changed";

    public static string Render(string next)
    {
        var target = IsLocal(next) ? next : "/";
        var html = WebUtility.HtmlEncode(target);
        var js = JavaScriptEncoder.Default.Encode(target);
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="refresh" content="1;url={{html}}">
            <title>Signed out - Blocwerk</title>
            </head>
            <body>
            <p>Signed out. <a href="{{html}}">Continue</a></p>
            <script src="/js/deploy-drafts.js"></script>
            <script>
            try { window.dispatchEvent(new Event('{{IdentityChangedEvent}}')); } catch (e) { }
            try { if (window.bwDeployDrafts && window.bwDeployDrafts.clearAll) { window.bwDeployDrafts.clearAll(); } } catch (e) { }
            location.replace("{{js}}");
            </script>
            </body>
            </html>
            """;
    }

    private static bool IsLocal(string path) =>
        path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal);
}
