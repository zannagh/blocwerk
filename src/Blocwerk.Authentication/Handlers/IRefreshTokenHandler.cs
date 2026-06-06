using System.Security.Claims;
using Blocwerk.Core.Entities;

namespace Blocwerk.Authentication.Handlers;

public interface IRefreshTokenHandler
{
    Task<RefreshToken> GenerateRefreshTokenAsync(string jwtSecret, string jwtIssuer, ClaimsIdentity? claimsIdentity, TimeSpan lifetime);

    Task<ClaimsIdentity?> ValidateRefreshTokenAsync(string refreshToken);

    Task<bool> ValidateRefreshTokenExpiryAsync(string refreshToken);

    Task InvalidateRefreshTokenAsync(string refreshToken);
}
