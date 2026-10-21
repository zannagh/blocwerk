// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Text.Encodings.Web;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// The markup of <see cref="SiteDataClearing.SignedOutPage"/>: a bare page that tells the page-side caches the identity
/// changed and moves on at once. It loads <c>deploy-drafts.js</c>, whose <c>bw:identity-changed</c> listener drops
/// the editor copies kept on the device for the identity that left, and <c>asset-ledger.js</c> + <c>asset-wipe.js</c>,
/// whose <c>bwAssetWipe.signedOut()</c> wipes the on-device asset cache before it moves on (capped at 1.5 s, inside the
/// 2 s meta refresh; the next page's partition check wipes anyway). Its target is always a local path; anything else becomes "/".
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
            <meta http-equiv="refresh" content="2;url={{html}}">
            <title>Signed out - Blocwerk</title>
            </head>
            <body>
            <p>Signed out. <a href="{{html}}">Continue</a></p>
            <script src="/js/deploy-drafts.js"></script>
            <script src="/js/asset-ledger.js"></script>
            <script src="/js/asset-wipe.js"></script>
            <script>
            try { window.dispatchEvent(new Event('{{IdentityChangedEvent}}')); } catch (e) { }
            try { if (window.bwDeployDrafts && window.bwDeployDrafts.clearAll) { window.bwDeployDrafts.clearAll(); } } catch (e) { }
            var gone = false;
            var go = function () { if (!gone) { gone = true; location.replace("{{js}}"); } };
            setTimeout(go, 1500);
            try { window.bwAssetWipe.signedOut().then(go, go); } catch (e) { go(); }
            </script>
            </body>
            </html>
            """;
    }

    private static bool IsLocal(string path) =>
        path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.StartsWith("/\\", StringComparison.Ordinal);
}
