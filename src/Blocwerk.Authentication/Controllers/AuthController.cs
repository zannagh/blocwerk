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

        if (!_redirectUriProvider.GetRedirectUri(state, out RedirectSettings redirectUri))
        {
            return BadRequest("Unknown state parameter.");
        }

        if (string.IsNullOrEmpty(code))
        {
            return BadRequest("Missing code parameter.");
        }

        _codeBasedAuthProvider.AddCodeIdentityProvider(code, redirectUri.Provider);

        return Redirect($"{redirectUri.Uri}?state={state}&code={code}");
    }

    [HttpPost("token")]
    [AllowAnonymous]
    [Produces("application/json")]
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

        var identity = ClaimsHelper.ClaimsIdentityFromUserNameAndId(validationResult.UserName, validationResult.UserId);
        AccessTokenResult token = await _tokenHandler.GenerateJwtTokenAsync(_settings.JwtKey, _settings.Server.JwtIssuer, _settings.JwtTokenLifetime, identity);
        return Ok(token);
    }
}
