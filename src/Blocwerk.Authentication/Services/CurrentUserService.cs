using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Blocwerk.Authentication.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor? _accessor;
    private readonly AuthenticationStateProvider? _authenticationStateProvider;
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly IPasswordLoginService _passwordLoginService;
    private readonly ITotpService _totpService;
    private readonly BlocwerkSettings _settings;

    // Scoped service = one instance per circuit / HTTP request. The signed-in identity is stable for
    // that lifetime (sign-in/out does a full reload that starts a fresh scope), so resolve the User
    // once and reuse it. Without this a single page render fanned out to 5-6 identical Users lookups
    // (GetWall + GetSegments + GetBoulderList + GetActivity + GetHoldUsage each re-queried the user).
    private User? _cachedUser;

    public CurrentUserService(
        BlocwerkSettings settings,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IPasswordLoginService passwordLoginService,
        ITotpService totpService,
        AuthenticationStateProvider? authenticationStateProvider = null,
        IHttpContextAccessor? accessor = null)
    {
        _accessor = accessor;
        _authenticationStateProvider = authenticationStateProvider;
        _dbContextFactory = dbContextFactory;
        _passwordLoginService = passwordLoginService;
        _totpService = totpService;
        _settings = settings;
    }

    public void InvalidateCache() => _cachedUser = null;

    public async Task SetHomeWallAsync(Guid? wallId)
    {
        var user = await GetCurrentUserAsync();

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        if (wallId is { } targetWallId)
        {
            bool isMember = await dbContext.WallMembers
                .AnyAsync(m => m.WallId == targetWallId && m.UserId == user.Id);
            if (!isMember)
            {
                throw new InvalidOperationException(
                    $"Cannot set home wall {targetWallId}: user {user.Id} is not a member of that wall.");
            }
        }

        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);
        dbUser.HomeWallId = wallId;
        await dbContext.SaveChangesAsync();

        InvalidateCache();
    }

    public async Task SetPreferFontGradesAsync(bool preferFont)
    {
        var user = await GetCurrentUserAsync();

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);
        dbUser.PreferFontGrades = preferFont;
        await dbContext.SaveChangesAsync();

        InvalidateCache();
    }

    private const int MaxDisplayNameLength = 256;

    // Blazor Server streams the upload over SignalR, so a generous ceiling is fine here — the image is
    // downscaled to at most AvatarMaxEdge px before it is ever persisted, so the stored bytes stay tiny.
    private const long MaxAvatarBytes = 30L * 1024 * 1024;
    private const int AvatarMaxEdge = 512;

    private static readonly HashSet<string> AllowedAvatarContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
    };

    public async Task SetDisplayNameAsync(string? name)
    {
        var user = await GetCurrentUserAsync();

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            trimmed = null;
        }
        else if (trimmed.Length > MaxDisplayNameLength)
        {
            trimmed = trimmed[..MaxDisplayNameLength];
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);
        dbUser.CustomDisplayName = trimmed;
        await dbContext.SaveChangesAsync();

        InvalidateCache();
    }

    public async Task SetAvatarAsync(byte[]? image, string? contentType)
    {
        var user = await GetCurrentUserAsync();

        byte[]? storedImage = null;
        string? storedContentType = null;

        if (image is { Length: > 0 })
        {
            if (image.LongLength > MaxAvatarBytes)
            {
                throw new InvalidOperationException("Avatar image is too large (max 30 MB).");
            }

            if (string.IsNullOrEmpty(contentType) || !AllowedAvatarContentTypes.Contains(contentType))
            {
                throw new InvalidOperationException("Avatar image must be a JPEG, PNG or WebP file.");
            }

            // Downscale + re-encode server-side so avatars are stored small regardless of the source
            // resolution. Original bytes may be many MB from a phone camera; the stored copy is a
            // <=512px PNG. A decode failure surfaces to the caller's error field.
            (storedImage, storedContentType) = ScaleAvatar(image);
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);

        dbUser.AvatarImage = storedImage;
        dbUser.AvatarContentType = storedContentType;

        await dbContext.SaveChangesAsync();

        InvalidateCache();
    }

    // Decodes the uploaded image, scales it so its longest edge is at most AvatarMaxEdge (preserving
    // aspect ratio; smaller images are left at their pixel size), and re-encodes it as PNG. Throws
    // InvalidOperationException when the bytes can't be decoded so the UI can show its avatar error.
    private static (byte[] Image, string ContentType) ScaleAvatar(byte[] image)
    {
        using var decoded = SKBitmap.Decode(image);
        if (decoded == null)
        {
            throw new InvalidOperationException("Couldn't read that image. Please try a JPEG, PNG or WebP file.");
        }

        var longEdge = Math.Max(decoded.Width, decoded.Height);
        SKBitmap source = decoded;
        SKBitmap? scaled = null;
        if (longEdge > AvatarMaxEdge)
        {
            var scale = (double)AvatarMaxEdge / longEdge;
            var w = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            var h = Math.Max(1, (int)Math.Round(decoded.Height * scale));
            scaled = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            decoded.ScalePixels(scaled, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            source = scaled;
        }

        try
        {
            using var pixmapImage = SKImage.FromBitmap(source);
            using var data = pixmapImage.Encode(SKEncodedImageFormat.Png, 90);
            return (data.ToArray(), "image/png");
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    public async Task SetPasswordAsync(string loginUsername, string password, string? currentPassword)
    {
        // Resolve the signed-in user first: this asserts an authenticated identity and gives the id
        // the credential is attached to. There is no path here that creates a user.
        var user = await GetCurrentUserAsync();

        await _passwordLoginService.SetPasswordAsync(user.Id, loginUsername, password, currentPassword);

        InvalidateCache();
    }

    public async Task<TotpEnrollment> BeginTotpEnrollmentAsync()
    {
        var user = await GetCurrentUserAsync();

        // TOTP is a second factor on top of the password, never a standalone credential: refuse to enrol
        // until a password exists so the account can never end up with 2FA but no first factor.
        if (!user.HasPassword)
        {
            throw new InvalidOperationException("Set a password before enabling two-factor authentication.");
        }

        var secret = _totpService.GenerateSecret();
        var label = string.IsNullOrWhiteSpace(user.LoginUsername) ? user.Name : user.LoginUsername;
        var uri = _totpService.BuildOtpAuthUri(secret, label);
        var qr = _totpService.BuildQrPng(uri);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);

        // Store the secret PROTECTED and keep TotpEnabled false — this enrolment is pending until a code
        // is confirmed. Overwriting any previous pending secret is intentional (a restarted enrolment).
        dbUser.TotpSecretProtected = _totpService.Protect(secret);
        dbUser.TotpEnabled = false;
        await dbContext.SaveChangesAsync();

        InvalidateCache();
        return new TotpEnrollment(secret, uri, qr);
    }

    public async Task<bool> ConfirmTotpAsync(string code)
    {
        var user = await GetCurrentUserAsync();

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);

        if (string.IsNullOrEmpty(dbUser.TotpSecretProtected))
        {
            return false;
        }

        string secret;
        try
        {
            secret = _totpService.Unprotect(dbUser.TotpSecretProtected);
        }
        catch (Exception)
        {
            return false;
        }

        if (!_totpService.Verify(secret, code))
        {
            return false;
        }

        dbUser.TotpEnabled = true;
        await dbContext.SaveChangesAsync();

        InvalidateCache();
        return true;
    }

    public async Task<bool> DisableTotpAsync(string code)
    {
        var user = await GetCurrentUserAsync();

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);

        // Step-up re-auth: a current, valid authenticator code is required to turn 2FA off, so a hijacked
        // session cannot strip the second factor. Refuse (without change) when TOTP isn't actually on.
        if (!dbUser.TotpEnabled || string.IsNullOrEmpty(dbUser.TotpSecretProtected))
        {
            return false;
        }

        string secret;
        try
        {
            secret = _totpService.Unprotect(dbUser.TotpSecretProtected);
        }
        catch (Exception)
        {
            return false;
        }

        if (!_totpService.Verify(secret, code))
        {
            return false;
        }

        dbUser.TotpSecretProtected = null;
        dbUser.TotpEnabled = false;
        dbUser.TotpLastUsedStep = null;
        await dbContext.SaveChangesAsync();

        InvalidateCache();
        return true;
    }

    public async Task<User?> GetUserByIdAsync(Guid id)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }

    public async Task<IReadOnlyList<string>> GetLinkedProvidersAsync()
    {
        var user = await GetCurrentUserAsync();

        // Only UserIdentities can name a provider. A legacy account's ORIGINAL provider is NOT recorded
        // anywhere — the legacy Identifier ("{name}__{sub}") stores the subject but no provider name, and
        // no other column or claim carries it — so a provider used ONLY on a legacy account (never
        // re-linked) cannot be shown as "Linked" here and is still offered as "Link". That is harmless:
        // the first login/link through that provider back-fills its UserIdentity (login) or a legacy
        // merge (link), after which it registers correctly. A sub-format heuristic would be fragile and
        // is deliberately avoided.
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        return await dbContext.UserIdentities
            .AsNoTracking()
            .Where(i => i.UserId == user.Id)
            .Select(i => i.Provider)
            .ToListAsync();
    }

    public async Task<User> GetCurrentUserAsync()
    {
        if (_cachedUser is not null)
        {
            return _cachedUser;
        }

        var claimsIdentity = await TryGetClaimsIdentityFromCookie()
                             ?? TryGetClaimsIdentityFromHttpContext();

        if (claimsIdentity == null)
        {
            throw new UnauthorizedAccessException();
        }

        string identifier = claimsIdentity.ToUserIdentifier();

        if (string.IsNullOrEmpty(identifier))
        {
            throw new UnauthorizedAccessException();
        }

        // The provider ("github"/"google"/"microsoft") is present for OAuth logins and absent for
        // legacy cookies and dev login. providerUserId is the stable provider subject (nameid).
        string provider = claimsIdentity.GetProvider();
        string providerUserId = claimsIdentity.ToUserId();

        // Password (and TOTP) sign-ins stamp the exact user id as a "uid" claim. OAuth logins carry none.
        string uid = claimsIdentity.FindFirst("uid")?.Value ?? string.Empty;

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        var user = await ResolveUserAsync(dbContext, identifier, provider, providerUserId, uid);

        // AdminIdentifiers is authoritative for the app-wide admin bit in BOTH directions: on every
        // resolution Role == Admin IFF the identifier is configured. Promote a configured admin that
        // isn't yet Admin, and — critically — demote an identifier that is Admin but no longer
        // configured, so removing someone from AdminIdentifiers actually revokes their admin. Only the
        // Admin/User toggle is touched here (Guest and other role semantics are left alone), and the
        // branches are mutually exclusive so a single resolution never promotes and demotes.
        bool shouldBeAdmin = _settings.AdminIdentifiers.Contains(identifier);
        if (shouldBeAdmin && user.Role != IdentityRole.Admin)
        {
            user.Role = IdentityRole.Admin;
            await dbContext.SaveChangesAsync();
        }
        else if (!shouldBeAdmin && user.Role == IdentityRole.Admin)
        {
            user.Role = IdentityRole.User;
            await dbContext.SaveChangesAsync();
        }

        _cachedUser = user;
        return user;
    }

    /// <summary>
    /// Resolves the current <see cref="User"/> from the login claims, attaching a
    /// <see cref="UserIdentity"/> lazily. This is the ONLY user-creation path: a user is only ever
    /// born from an OAuth login. Resolution order:
    /// (0) a "uid" claim (stamped by password/TOTP sign-in) → that exact user by id, never creating one.
    ///     This makes password sessions resolve precisely and sidesteps the lossy "{name}__{id}"
    ///     identifier round-trip, which misresolves (and would otherwise CREATE a blank user) when the
    ///     name itself contains "__";
    /// (a)/(b) for an OAuth login, resolve through the shared <see cref="LegacyIdentityResolver"/>
    ///     (UserIdentity row → legacy Identifier subject-suffix), back-filling a
    ///     <see cref="UserIdentity"/> when the match came from a legacy row. Sharing this resolver with
    ///     the account-link path is what keeps login and linking from diverging on identity ownership;
    /// (b') no provider claim (dev login / legacy cookie) → resolve purely by the full identifier;
    /// (c) no user at all → create the user (as before) plus, for an OAuth login, its identity.
    /// </summary>
    private static async Task<User> ResolveUserAsync(BlocwerkDbContext dbContext, string identifier, string provider, string providerUserId, string uid)
    {
        // (0) Exact resolution by uid claim for password/TOTP sessions — first, and never creates a user.
        if (!string.IsNullOrEmpty(uid) && Guid.TryParse(uid, out var uidGuid))
        {
            var byId = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == uidGuid);
            if (byId is not null)
            {
                return byId;
            }
        }

        bool hasProvider = !string.IsNullOrEmpty(provider) && !string.IsNullOrEmpty(providerUserId);

        // (a)/(b) Resolve the OAuth identity through the ONE shared resolver, then back-fill a
        // UserIdentity so a legacy match becomes a first-class identity row (no-op if it already exists).
        if (hasProvider)
        {
            var owner = await LegacyIdentityResolver.FindByProviderIdentityAsync(dbContext, provider, providerUserId);
            if (owner is not null)
            {
                await EnsureIdentityAsync(dbContext, owner.Id, provider, providerUserId);
                return owner;
            }
        }

        // (b') No provider claim (dev login / legacy cookie): resolve purely by the full identifier.
        // (An OAuth login that reaches here found no identity and no legacy subject match, so the
        // full-identifier lookup below would not match it either — it falls through to creation.)
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Identifier == identifier);
        if (user is not null)
        {
            return user;
        }

        // (c) No user exists yet — create it (OAuth-gated signup), plus its provider identity.
        user = new User
        {
            Identifier = identifier,
            DisplayName = identifier.Split("__").FirstOrDefault() ?? identifier,
            Role = IdentityRole.User,
        };
        await dbContext.Users.AddAsync(user);
        await dbContext.SaveChangesAsync();

        if (hasProvider)
        {
            await EnsureIdentityAsync(dbContext, user.Id, provider, providerUserId);
        }

        return user;
    }

    private static async Task EnsureIdentityAsync(BlocwerkDbContext dbContext, Guid userId, string provider, string providerUserId)
    {
        bool exists = await dbContext.UserIdentities
            .AnyAsync(i => i.Provider == provider && i.ProviderUserId == providerUserId);
        if (exists)
        {
            return;
        }

        await dbContext.UserIdentities.AddAsync(new UserIdentity
        {
            UserId = userId,
            Provider = provider,
            ProviderUserId = providerUserId,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task<ClaimsIdentity?> TryGetClaimsIdentityFromCookie()
    {
        if (_authenticationStateProvider == null)
        {
            return null;
        }

        var cookieState = await _authenticationStateProvider.GetAuthenticationStateAsync();

        if (cookieState.User is not { Identity.IsAuthenticated: true }
            || cookieState.User.FindFirst(ClaimTypes.NameIdentifier) is not { } nameIdentifier
            || cookieState.User.FindFirst(ClaimTypes.Name) is not { } name)
        {
            return null;
        }

        var cookieClaim = new ClaimsIdentity();
        cookieClaim.AddClaim(new Claim(ClaimTypes.NameIdentifier, nameIdentifier.Value));
        cookieClaim.AddClaim(new Claim(ClaimTypes.Name, name.Value));

        // Carry the "uid" claim through when present (password/TOTP sign-ins) so resolution can look the
        // user up by their exact id — this is what keeps a password session from misresolving (and
        // creating a blank user) when the display name contains "__".
        if (cookieState.User.FindFirst("uid") is { } uidClaim && !string.IsNullOrEmpty(uidClaim.Value))
        {
            cookieClaim.AddClaim(new Claim("uid", uidClaim.Value));
        }

        // Carry the provider claim through when present (OAuth logins) so resolution can look the user
        // up by their provider identity. Absent for legacy cookies signed before this change and for
        // dev login, which then fall back to identifier-based resolution.
        if (cookieState.User.FindFirst("provider") is { } providerClaim
            && !string.IsNullOrEmpty(providerClaim.Value))
        {
            cookieClaim.AddClaim(new Claim("provider", providerClaim.Value));
        }

        return cookieClaim;
    }

    private ClaimsIdentity? TryGetClaimsIdentityFromHttpContext()
    {
        if (_accessor?.HttpContext is not { } httpContext)
        {
            return null;
        }

        // An API key resolves to its OWNER, which then opens every wall that owner belongs to via
        // the membership query filter. That is only ever acceptable on an endpoint that explicitly
        // opted into authorization — those compare the route's wall against the key's own wall
        // claim (WallScopedApiController.GuardWall) or are scoped to the owner by definition
        // (/api/v1/me/*). An unguarded endpoint that merely happens to sit under /api/walls did no
        // such check, so the key must not resolve a user there at all.
        if (httpContext.User.IsApiKeyPrincipal() && !ApiKeySurface.HasExplicitAuthorization(httpContext))
        {
            return null;
        }

        if (httpContext.User.Identity is ClaimsIdentity { IsAuthenticated: true } httpClaimsIdentity)
        {
            return httpClaimsIdentity;
        }

        return null;
    }
}
