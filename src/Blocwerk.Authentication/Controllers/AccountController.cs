using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Providers;
using Blocwerk.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Authentication.Controllers;

public class AccountController : Controller
{
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
    public IActionResult Login(string? returnUrl = null)
    {
        if (!string.IsNullOrEmpty(returnUrl))
        {
            TempData["ReturnUrl"] = returnUrl;
        }

        var queryParams = new Dictionary<string, string>
        {
            ["redirect_uri"] = $"{BaseUrl}/account/callback",
        };

        var queryString = string.Join("&", queryParams.Select(kvp =>
            $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));

        return Redirect($"/oauth-select?{queryString}");
    }

    [HttpGet("/account/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string state)
    {
        if (string.IsNullOrEmpty(code))
        {
            return BadRequest("Missing authorization code");
        }

        try
        {
            FormUrlEncodedContent tokenRequest = new(
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("client_id", GetClientIdFromState(state)),
                new KeyValuePair<string, string>("client_secret", GetClientSecretFromState(state)),
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
        return LocalRedirect("/");
    }

    private string GetClientIdFromState(string? state)
    {
        if (string.IsNullOrEmpty(state) ||
            !_redirectUriProvider.GetRedirectUri(state, out var redirectSettings))
        {
            return _configuration.GitHubOAuth.ClientId;
        }

        return redirectSettings.Provider switch
        {
            "github" => _configuration.GitHubOAuth.ClientId,
            "google" => _configuration.GoogleOAuth.ClientId,
            "microsoft" => _configuration.MicrosoftOAuth.ClientId,
            _ => _configuration.GitHubOAuth.ClientId,
        };
    }

    private string GetClientSecretFromState(string? state)
    {
        if (string.IsNullOrEmpty(state) ||
            !_redirectUriProvider.GetRedirectUri(state, out var redirectSettings))
        {
            return _configuration.GitHubOAuth.ClientSecret;
        }

        return redirectSettings.Provider switch
        {
            "github" => _configuration.GitHubOAuth.ClientSecret,
            "google" => _configuration.GoogleOAuth.ClientSecret,
            "microsoft" => _configuration.MicrosoftOAuth.ClientSecret,
            _ => _configuration.GitHubOAuth.ClientSecret,
        };
    }
}
