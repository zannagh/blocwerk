using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Authentication.Controllers;

/// <summary>
/// The single place every redirect target that a visitor had a hand in building passes through.
/// </summary>
/// <remarks>
/// Two shapes exist and neither may take the caller's word for where to go:
/// <list type="bullet">
/// <item>a target inside the app (a <c>returnUrl</c> carried through the sign-in round-trip) — only
/// a local path is honoured, anything absolute or protocol-relative falls back;</item>
/// <item>a target at an identity provider — only an origin the installation is actually configured
/// to use is honoured, so a crafted <c>provider</c> can never steer the browser somewhere else.</item>
/// </list>
/// </remarks>
public partial class AccountController
{
    /// <summary>
    /// Redirects to <paramref name="target"/> when it is a local path, otherwise to
    /// <paramref name="fallback"/>. <paramref name="fallback"/> is always a literal in the caller.
    /// </summary>
    private IActionResult RedirectLocalOr(string? target, string fallback = "/")
    {
        if (!string.IsNullOrEmpty(target) && Url.IsLocalUrl(target))
        {
            return Redirect(target);
        }

        return Redirect(fallback);
    }

    /// <summary>
    /// Redirects to the OAuth authorize endpoint of <paramref name="provider"/>, or to
    /// <paramref name="fallback"/> (a local path) when the provider is unknown, disabled, or the
    /// built URL does not sit on a configured provider origin.
    /// </summary>
    private IActionResult RedirectToProviderOr(string provider, string state, string fallback)
    {
        var target = BuildProviderAuthorizeUrl(provider, state);
        if (target is null || !IsConfiguredProviderUrl(target))
        {
            return RedirectLocalOr(fallback);
        }

        return Redirect(target);
    }

    /// <summary>
    /// True when <paramref name="url"/> is absolute and its scheme/host/port match one of the OAuth
    /// endpoints this installation has enabled. The allow-list is the configuration, never the request.
    /// </summary>
    private bool IsConfiguredProviderUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        foreach (var allowed in ConfiguredProviderAuthUrls())
        {
            if (Uri.TryCreate(allowed, UriKind.Absolute, out var known)
                && Uri.Compare(
                    candidate,
                    known,
                    UriComponents.SchemeAndServer,
                    UriFormat.Unescaped,
                    StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The authorize endpoints of the providers this installation has enabled.</summary>
    private IEnumerable<string> ConfiguredProviderAuthUrls()
    {
        if (_configuration.GitHubOAuth.Enabled)
        {
            yield return _configuration.GitHubOAuth.OAuthUrl;
        }

        if (_configuration.MicrosoftOAuth.Enabled)
        {
            yield return _configuration.MicrosoftOAuth.OAuthUrl;
        }

        if (_configuration.GoogleOAuth.Enabled)
        {
            yield return _configuration.GoogleOAuth.OAuthUrl;
        }
    }
}
