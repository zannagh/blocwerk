using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// Decides whether a request's <c>Authorization: Bearer bwk_…</c> header may turn into a BROWSER
/// session for the key's owner, and whether an existing key session may continue. Used by
/// <c>POST /account/api-key-login</c>, the Development-only <c>/dev/login</c> and
/// <see cref="ApiKeySessionValidator"/>.
/// </summary>
/// <remarks>
/// Far narrower than <see cref="Handlers.ApiKeyAuthenticationHandler"/>, because the outcome is far
/// wider: that handler authenticates one request on the machine API surface, this one mints a full
/// cookie login. Only a PERSONAL key (<see cref="ApiKeyScope.User"/>, no wall) of a live, real user
/// on the operator's allow-list qualifies. Wall and kiosk keys live on devices bolted to walls and
/// must be assumed to leak; an installation key belongs to the server, and its <c>UserId</c> only
/// records who minted it. The token is read from the header ONLY: a query string or form field ends
/// up in URLs, proxy logs and browser history.
/// </remarks>
public sealed class ApiKeyLoginValidator
{
    private const string BearerPrefix = "Bearer ";

    private readonly IApiKeyService apiKeyService;
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IKioskContext kioskContext;
    private readonly BlocwerkSettings settings;

    public ApiKeyLoginValidator(
        IApiKeyService apiKeyService,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IKioskContext kioskContext,
        BlocwerkSettings settings)
    {
        this.apiKeyService = apiKeyService;
        this.dbContextFactory = dbContextFactory;
        this.kioskContext = kioskContext;
        this.settings = settings;
    }

    /// <summary>Reads the bearer API key from the Authorization header, or null when there is none.</summary>
    public static string? ReadBearerApiKey(HttpRequest request)
    {
        var headers = request.Headers.Authorization;
        if (headers.Count != 1)
        {
            return null;
        }

        var header = headers[0];
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[BearerPrefix.Length..].Trim();
        return ApiKeyTokens.LooksLikeApiKey(token) ? token : null;
    }

    public async Task<ApiKeyLoginResult> ValidateAsync(HttpRequest request, CancellationToken ct)
    {
        var token = ReadBearerApiKey(request);
        if (token is null)
        {
            return ApiKeyLoginResult.Failure("no bearer API key in the Authorization header");
        }

        // A device carrying the kiosk registration cookie is a kiosk whoever signs in on it (see
        // KioskContext.Apply), so a login minted here would be a kiosk identity. Refuse outright.
        await kioskContext.InitializeAsync();
        if (kioskContext.IsKiosk)
        {
            return ApiKeyLoginResult.Failure("request comes from a kiosk device");
        }

        // The API-key scheme's own lookup (SHA-256 of the token; revoked and expired keys refused),
        // but WITHOUT stamping LastUsedAt: a use is only recorded once every check below has passed.
        var key = await apiKeyService.FindActiveAsync(token, ct);
        if (key is null)
        {
            return ApiKeyLoginResult.Failure("unknown, revoked or expired key");
        }

        // Belt and braces over the database equality match: compare the digests in constant time.
        if (!HashesMatch(token, key.KeyHash))
        {
            return ApiKeyLoginResult.Failure("key hash mismatch", key);
        }

        var result = await CheckKeyAndOwnerAsync(key, ct);
        if (result.Succeeded)
        {
            await apiKeyService.MarkUsedAsync(key, ct);
        }

        return result;
    }

    /// <summary>
    /// Whether the key session signed in with <paramref name="keyId"/> for <paramref name="userId"/>
    /// may continue: the feature is still on, the key still exists, is neither revoked nor expired,
    /// is still a personal key of that same user, and the user still qualifies.
    /// </summary>
    public async Task<bool> IsSessionStillValidAsync(Guid keyId, Guid userId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;
        var key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId, ct);

        var now = DateTimeOffset.UtcNow;
        if (key is null
            || key.UserId != userId
            || key.RevokedAt is not null
            || (key.ExpiresAt is { } expires && expires <= now))
        {
            return false;
        }

        return (await CheckKeyAndOwnerAsync(key, ct)).Succeeded;
    }

    private static bool HashesMatch(string token, string storedHash)
    {
        var actual = Encoding.ASCII.GetBytes(ApiKeyTokens.Hash(token));
        var expected = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private async Task<ApiKeyLoginResult> CheckKeyAndOwnerAsync(ApiKey key, CancellationToken ct)
    {
        var options = settings.Auth.ApiKeyLogin;
        if (!options.Enabled)
        {
            return ApiKeyLoginResult.Failure("API key login is disabled", key);
        }

        if (key.Scope != ApiKeyScope.User || key.WallId is not null)
        {
            return ApiKeyLoginResult.Failure($"{key.Scope} key is not a personal key", key);
        }

        // The operator's explicit list. Empty means nobody, even with the feature switched on. Being
        // listed is also the operator's decision that this user may skip their second factor here.
        if (!options.AllowedUserIds.Contains(key.UserId))
        {
            return ApiKeyLoginResult.Failure("key owner is not on the API key login allow-list", key);
        }

        var user = await LoadEligibleUserAsync(key.UserId, ct);
        return user is null
            ? ApiKeyLoginResult.Failure("key owner is not an active, real user", key)
            : ApiKeyLoginResult.Success(user, key);
    }

    /// <summary>
    /// The key's owner, when that owner is somebody a browser session may be minted for: a live row
    /// (not an erased account's tombstone), not the seeded Ghost system row, and not locked out.
    /// </summary>
    private async Task<User?> LoadEligibleUserAsync(Guid userId, CancellationToken ct)
    {
        if (GhostUser.Is(userId))
        {
            return null;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = Guid.Empty;

        var user = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAt == null, ct);

        if (user is null
            || GhostUser.IsSystemIdentifier(user.Identifier)
            || user.Identifier == PlaceholderIdentity.DeletedIdentifier(user.Id))
        {
            return null;
        }

        // The password lockout is a signal that somebody is attacking this account right now; a
        // second way in must not stay open while the first is shut.
        if (user.LockoutUntil is { } until && until > DateTimeOffset.UtcNow)
        {
            return null;
        }

        return user;
    }
}
