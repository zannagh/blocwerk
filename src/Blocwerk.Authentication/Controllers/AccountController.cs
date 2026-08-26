using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Resources;
using Blocwerk.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Authentication.Controllers;

public class AccountController : Controller
{
    // Records the provider a returning visitor asked us to remember, so /account/login can re-auth
    // silently instead of showing the selection page again.
    private const string RememberedMethodCookie = "bw_login_method";

    private readonly BlocwerkSettings _configuration;
    private readonly RedirectUriProvider _redirectUriProvider;
    private readonly IHttpClientFactory _httpClientFactory;

    public AccountController(
        BlocwerkSettings settings,
        RedirectUriProvider redirectUriProvider,
        IHttpClientFactory httpClientFactory)
    {
        _configuration = settings;
        _redirectUriProvider = redirectUriProvider;
        _httpClientFactory = httpClientFactory;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    [HttpGet("/account/login")]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null, string? error = null)
    {
        // Only carry a local returnUrl. A crafted absolute/cross-site value can't cause an open
        // redirect (LocalRedirect guards the return leg), but dropping it here fails fast and keeps a
        // bad value from riding through the whole flow only to bounce to an error page at the end.
        if (!string.IsNullOrEmpty(returnUrl) && !Url.IsLocalUrl(returnUrl))
        {
            returnUrl = null;
        }

        if (!string.IsNullOrEmpty(returnUrl))
        {
            TempData["ReturnUrl"] = returnUrl;
        }

        // A failed login returns here with ?error — always fall back to the selection page in that
        // case so silent re-auth can never spin into a redirect loop.
        if (!string.IsNullOrEmpty(error))
        {
            return Redirect(BuildOAuthSelectUrl(returnUrl, error));
        }

        // Silent re-auth: if the visitor previously chose "remember my sign-in method" and that
        // provider is still enabled, challenge it straight away and skip the selection page.
        if (Request.Cookies.TryGetValue(RememberedMethodCookie, out var remembered)
            && !string.IsNullOrEmpty(remembered)
            && GetProviderAuthConfig(remembered) is not null)
        {
            var target = $"/account/external?provider={Uri.EscapeDataString(remembered)}";
            if (!string.IsNullOrEmpty(returnUrl))
            {
                target += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
            }

            return Redirect(target);
        }

        // Carry returnUrl to the selection page so the provider buttons preserve it too — the click
        // path then matches the silent path instead of relying on TempData alone.
        return Redirect(BuildOAuthSelectUrl(returnUrl, error: null));
    }

    // The provider-selection page (/oauth-select) links straight here as a plain GET, so choosing a
    // provider is a single server round-trip that 302s to the provider — no Blazor circuit, no
    // double-click race. Mirrors what the old interactive OAuthSelect.RedirectToProvider did.
    [HttpGet("/account/external")]
    [AllowAnonymous]
    public IActionResult ExternalLogin([FromQuery] string provider, [FromQuery] string? returnUrl = null, [FromQuery] string? remember = null)
    {
        var config = GetProviderAuthConfig(provider);
        if (config is null)
        {
            return Redirect("/oauth-select");
        }

        if (!string.IsNullOrEmpty(returnUrl))
        {
            TempData["ReturnUrl"] = returnUrl;
        }

        // Persist or clear the "remember my sign-in method" preference from the checkbox. This is the
        // only point that reliably knows both the provider and the choice: the OAuth state is consumed
        // upstream by /oauth-callback, so it is already gone by the time /account/callback runs. A
        // missing flag (the silent re-auth path) leaves any existing preference untouched.
        if (remember is "1" or "true")
        {
            Response.Cookies.Append(RememberedMethodCookie, provider, RememberedMethodCookieOptions());
        }
        else if (remember is "0" or "false")
        {
            Response.Cookies.Delete(RememberedMethodCookie);
        }

        var state = Guid.NewGuid().ToString();
        _redirectUriProvider.AddRedirectUri(state, new RedirectSettings
        {
            Uri = $"{BaseUrl}/account/callback",
            Provider = provider,
        });

        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = config.Value.ClientId,
            ["state"] = state,
            ["redirect_uri"] = $"{BaseUrl}/oauth-callback",
        };

        if (provider == "google")
        {
            parameters["response_type"] = "code";
            parameters["scope"] = "https://www.googleapis.com/auth/userinfo.email https://www.googleapis.com/auth/userinfo.profile";
        }
        else if (provider == "microsoft")
        {
            parameters["response_type"] = "code";
            parameters["scope"] = "User.Read";
        }

        var queryString = string.Join("&", parameters.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        return Redirect($"{config.Value.AuthUrl}?{queryString}");
    }

    [HttpGet("/account/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state)
    {
        if (string.IsNullOrEmpty(code))
        {
            // The user cancelled or denied at the provider (no code returned) — route through the
            // normal error path rather than dead-ending on a bare 400.
            return Redirect("/account/login?error=cancelled");
        }

        try
        {
            // The OAuth state is already consumed upstream by /oauth-callback, so it is gone here. The
            // client_id/client_secret below are only placeholders: /token re-derives the real provider
            // from the code (via CodeBasedAuthProvider) and overrides these, so their value is moot.
            FormUrlEncodedContent tokenRequest = new(
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("client_id", _configuration.GitHubOAuth.ClientId),
                new KeyValuePair<string, string>("client_secret", _configuration.GitHubOAuth.ClientSecret),
                new KeyValuePair<string, string>("redirect_uri", $"{BaseUrl}/oauth-callback"),
            ]);

            using var httpClient = _httpClientFactory.CreateClient();
            HttpResponseMessage tokenResponse = await httpClient.PostAsync($"{BaseUrl}/token", tokenRequest);

            if (!tokenResponse.IsSuccessStatusCode)
            {
                Log.Error("[Web Authentication] Token exchange failed with status {StatusCode}", tokenResponse.StatusCode);
                return Redirect("/account/login?error=auth_failed");
            }

            var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
            var tokenData = JsonSerializer.Deserialize<JsonElement>(tokenJson);

            if (!tokenData.TryGetProperty("access_token", out var accessTokenElement))
            {
                return Redirect("/account/login?error=token_missing");
            }

            var accessToken = accessTokenElement.GetString();
            if (string.IsNullOrEmpty(accessToken))
            {
                return Redirect("/account/login?error=empty_token");
            }

            var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
            var jsonToken = handler.ReadJwtToken(accessToken);

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, jsonToken.Claims.FirstOrDefault(x => x.Type == "nameid")?.Value ?? string.Empty),
                new(ClaimTypes.Name, jsonToken.Claims.FirstOrDefault(x => x.Type == "unique_name")?.Value ?? string.Empty),
                new("Name", jsonToken.Claims.FirstOrDefault(x => x.Type == "unique_name")?.Value ?? string.Empty),
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            var returnUrl = TempData["ReturnUrl"] as string ?? "/walls";
            return LocalRedirect(returnUrl);
        }
        catch (Exception ex)
        {
            Log.Error("[Web Authentication] Authentication callback error: {Message}", ex.Message);
            return Redirect("/account/login?error=callback_failed");
        }
    }

    [HttpGet("/account/logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // Drop the remembered method so an explicit logout isn't immediately undone by silent re-auth.
        Response.Cookies.Delete(RememberedMethodCookie);
        return LocalRedirect("/");
    }

    private static string BuildOAuthSelectUrl(string? returnUrl, string? error)
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(error))
        {
            query.Add($"error={Uri.EscapeDataString(error)}");
        }

        if (!string.IsNullOrEmpty(returnUrl))
        {
            query.Add($"returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        return query.Count == 0 ? "/oauth-select" : $"/oauth-select?{string.Join("&", query)}";
    }

    private CookieOptions RememberedMethodCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        MaxAge = TimeSpan.FromDays(365),
        Path = "/",
    };

    private (string AuthUrl, string ClientId)? GetProviderAuthConfig(string provider) => provider switch
    {
        "github" when _configuration.GitHubOAuth.Enabled => (_configuration.GitHubOAuth.OAuthUrl, _configuration.GitHubOAuth.ClientId),
        "microsoft" when _configuration.MicrosoftOAuth.Enabled => (_configuration.MicrosoftOAuth.OAuthUrl, _configuration.MicrosoftOAuth.ClientId),
        "google" when _configuration.GoogleOAuth.Enabled => (_configuration.GoogleOAuth.OAuthUrl, _configuration.GoogleOAuth.ClientId),
        _ => null,
    };
}
