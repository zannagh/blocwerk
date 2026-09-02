using Blocwerk.Authentication.Handlers;
using Blocwerk.Authentication.Providers;
using Blocwerk.Authentication.Resources;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace Blocwerk.Authentication.Controllers;

public class AuthController : ControllerBase
{
    private readonly BlocwerkSettings _settings;
    private readonly ISecurityTokenHandler _tokenHandler;
    private readonly IRefreshTokenHandler _refreshTokenHandler;
    private readonly RedirectUriProvider _redirectUriProvider;
    private readonly CodeBasedAuthProvider _codeBasedAuthProvider;

    public AuthController(
        BlocwerkSettings settings,
        ISecurityTokenHandler tokenHandler,
        IRefreshTokenHandler refreshTokenHandler,
        RedirectUriProvider redirectUriProvider,
        CodeBasedAuthProvider codeBasedAuthProvider)
    {
        _settings = settings;
        _tokenHandler = tokenHandler;
        _refreshTokenHandler = refreshTokenHandler;
        _redirectUriProvider = redirectUriProvider;
        _codeBasedAuthProvider = codeBasedAuthProvider;
    }

    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    [HttpGet("authorize")]
    [AllowAnonymous]
    public IActionResult Authorize([FromQuery] Dictionary<string, string>? inputQuery)
    {
        string queryString = string.Empty;
        if (inputQuery != null)
        {
            queryString = string.Join("&", inputQuery

                // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract - this can actually be null.
                .Where(k => k.Key != null && k.Value != null)
                .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
        }

        string redirectUri = string.IsNullOrEmpty(queryString)
            ? "/oauth-select"
            : $"/oauth-select?{queryString}";

        return LocalRedirect(redirectUri);
    }

    [HttpGet("/oauth-callback")]
    [AllowAnonymous]
    public IActionResult OAuthCallback(
        [FromQuery(Name = "state")] string state,
        [FromQuery(Name = "code")] string code)
    {
        if (string.IsNullOrEmpty(state))
        {
            return BadRequest("Missing state parameter.");
        }

        if (!_redirectUriProvider.GetRedirectUri(state, out var redirectUri))
        {
            return BadRequest("Unknown state parameter.");
        }

        if (string.IsNullOrEmpty(code))
        {
            return BadRequest("Missing code parameter.");
        }

        _codeBasedAuthProvider.AddCodeIdentityProvider(code, redirectUri.Provider);

        // Route through the static /signing-in spinner page rather than jumping straight to
        // /account/callback: the callback's token exchange takes ~1-2s, and the browser keeps the
        // spinner visible for that whole wait instead of showing a blank/tempting page. The page
        // forwards to /account/callback (a fixed local path — not taken from state, to avoid an
        // open redirect).
        return Redirect($"/signing-in?state={Uri.EscapeDataString(state)}&code={Uri.EscapeDataString(code)}");
    }

    /// <summary>The OAuth token endpoint: exchanges an authorization code or a refresh token for a JWT.</summary>
    /// <remarks>
    /// Antiforgery does not apply and must not: Antiforgery does not apply and must not: the caller authenticates
    /// with the code/refresh token and client secret it posts, never with an ambient cookie, so
    /// there is nothing a third-party page could ride on — and a token endpoint that demanded a
    /// browser-issued token would be unusable by the non-browser clients it exists for.
    /// </remarks>
    [HttpPost("token")]
    [AllowAnonymous]
    [Produces("application/json")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Token(
        [FromForm] string code,
        [FromForm(Name = "client_id")] string clientId,
        [FromForm(Name = "client_secret")] string clientSecret,
        [FromForm(Name = "redirect_uri")] string redirectUri,
        [FromForm(Name = "grant_type")] string grantType,
        [FromForm(Name = "code_verifier")] string codeVerifier,
        [FromForm(Name = "refresh_token")] string refreshToken = "")
    {
        if (string.IsNullOrEmpty(_settings.Server.JwtIssuer))
        {
            return StatusCode(500, "Missing JWT Issuer");
        }

        if (string.IsNullOrEmpty(_settings.JwtKey))
        {
            return StatusCode(500, "Missing JWT Secret");
        }

        if (grantType.ToLowerInvariant().Contains("refresh") && !string.IsNullOrEmpty(refreshToken))
        {
            if (await _refreshTokenHandler.ValidateRefreshTokenAsync(refreshToken) is not { } claimsIdentity)
            {
                return StatusCode(401, "Invalid refresh token");
            }

            try
            {
                await _refreshTokenHandler.InvalidateRefreshTokenAsync(refreshToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to invalidate refresh token");
            }

            return Ok(await _tokenHandler.GenerateJwtTokenAsync(_settings.JwtKey, _settings.Server.JwtIssuer, _settings.JwtTokenLifetime, claimsIdentity));
        }

        if (!_codeBasedAuthProvider.GetIdentityProviderByCode(code, out string? provider))
        {
            if (!redirectUri.Contains("localhost"))
            {
                return StatusCode(401, "Invalid code");
            }

            provider = "github";
        }

        clientId = provider switch
        {
            "github" => _settings.GitHubOAuth.ClientId,
            "google" => _settings.GoogleOAuth.ClientId,
            "microsoft" => _settings.MicrosoftOAuth.ClientId,
            _ => clientId,
        };
        clientSecret = provider switch
        {
            "github" => _settings.GitHubOAuth.ClientSecret,
            "google" => _settings.GoogleOAuth.ClientSecret,
            "microsoft" => _settings.MicrosoftOAuth.ClientSecret,
            _ => clientSecret,
        };

        VerificationResult validationResult = provider switch
        {
            "google" => await _tokenHandler.VerifyGoogleAuthentication(_settings, clientId, clientSecret, code,
                $"{BaseUrl}/oauth-callback", grantType, codeVerifier),
            "microsoft" => await _tokenHandler.VerifyMicrosoftAuthentication(_settings, clientId, clientSecret, code,
                $"{BaseUrl}/oauth-callback", grantType, codeVerifier),
            "github" => await _tokenHandler.VerifyGitHubAuthentication(_settings, clientId, clientSecret, code,
                grantType, codeVerifier),
            _ => new VerificationResult { Success = false, UserId = string.Empty, UserName = string.Empty },
        };

        if (!validationResult.Success)
        {
            return StatusCode(401);
        }

        if (string.IsNullOrEmpty(validationResult.UserId) || string.IsNullOrEmpty(validationResult.UserName))
        {
            return StatusCode(500, "Invalid user data received from OAuth provider.");
        }

        var identity = ClaimsHelper.ClaimsIdentityFromUserNameAndIdAndProvider(validationResult.UserName, validationResult.UserId, provider ?? string.Empty);
        AccessTokenResult token = await _tokenHandler.GenerateJwtTokenAsync(_settings.JwtKey, _settings.Server.JwtIssuer, _settings.JwtTokenLifetime, identity);
        return Ok(token);
    }
}
