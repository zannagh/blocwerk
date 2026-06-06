using System.Security.Claims;
using Blocwerk.Authentication.Resources;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Authentication.Handlers;

public interface ISecurityTokenHandler
{
    Task<VerificationResult> VerifyMicrosoftAuthentication(BlocwerkSettings settings, string clientId, string clientSecret, string code, string redirectUri, string grantType = "", string codeVerifier = "");

    Task<VerificationResult> VerifyGoogleAuthentication(BlocwerkSettings settings, string clientId, string clientSecret, string code, string redirectUri, string grantType = "", string codeVerifier = "");

    Task<VerificationResult> VerifyGitHubAuthentication(BlocwerkSettings settings, string clientId, string clientSecret, string code, string grantType = "", string codeVerifier = "");

    Task<AccessTokenResult> GenerateJwtTokenAsync(string jwtSecret, string jwtIssuer, TimeSpan lifetime, ClaimsIdentity? claimsIdentity);
}
