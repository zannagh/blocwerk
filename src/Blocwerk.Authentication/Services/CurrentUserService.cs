using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

public partial class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor? _accessor;
    private readonly AuthenticationStateProvider? _authenticationStateProvider;
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly IPasswordLoginService _passwordLoginService;
    private readonly ITotpService _totpService;
    private readonly BlocwerkSettings _settings;
    private readonly IKioskContext? _kioskContext;

    // Scoped service = one instance per circuit / HTTP request. The signed-in identity is stable for
    // that lifetime (sign-in/out does a full reload that starts a fresh scope), so resolve the User
    // once and reuse it. Without this a single page render fanned out to 5-6 identical Users lookups
    // (GetWall + GetSegments + GetBoulderList + GetActivity + GetHoldUsage each re-queried the user).
    private User? _cachedUser;

    // The mirror image of the cache above, for the other outcome. Resolution can also end in a
    // refusal — a tombstone, the Ghost row, a stale cookie for an account that no longer exists — and
    // that answer is just as stable for this scope. Without it the same 5-6 lookups each re-queried,
    // re-threw and re-signed-out, stacking a duplicate cookie-deletion header per call.
    private bool _resolutionRefused;

    public CurrentUserService(
        BlocwerkSettings settings,
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IPasswordLoginService passwordLoginService,
        ITotpService totpService,
        AuthenticationStateProvider? authenticationStateProvider = null,
        IHttpContextAccessor? accessor = null,
        IKioskContext? kioskContext = null)
    {
        _kioskContext = kioskContext;
        _accessor = accessor;
        _authenticationStateProvider = authenticationStateProvider;
        _dbContextFactory = dbContextFactory;
        _passwordLoginService = passwordLoginService;
        _totpService = totpService;
        _settings = settings;
    }

    public void InvalidateCache()
    {
        _cachedUser = null;
        _resolutionRefused = false;
    }

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

    public async Task SetShowToolsInNavAsync(bool show)
    {
        var user = await GetCurrentUserAsync();

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);
        dbUser.ShowToolsInNav = show;
        await dbContext.SaveChangesAsync();

        InvalidateCache();
    }

    public async Task SetNotificationDisabledAsync(NotificationType type, bool disabled)
    {
        var user = await GetCurrentUserAsync();

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);

        // The stored mask is opt-OUT: set the bit to disable the type, clear it to re-enable.
        if (disabled)
        {
            dbUser.DisabledNotifications |= type;
        }
        else
        {
            dbUser.DisabledNotifications &= ~type;
        }

        await dbContext.SaveChangesAsync();

        InvalidateCache();
    }

    private const int MaxDisplayNameLength = 256;

    // Blazor Server streams the upload over SignalR, so a generous ceiling is fine here — the image is
    // downscaled to at most AvatarImageEncoder.MaxEdge px before it is ever persisted, so the stored
    // bytes stay tiny.
    private const long MaxAvatarBytes = 30L * 1024 * 1024;

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
            // <=512px WebP. A decode failure surfaces to the caller's error field.
            (storedImage, storedContentType) = AvatarImageEncoder.Scale(image);
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var dbUser = await dbContext.Users.FirstAsync(u => u.Id == user.Id);

        dbUser.AvatarImage = storedImage;
        dbUser.AvatarContentType = storedContentType;

        await dbContext.SaveChangesAsync();

        InvalidateCache();
    }

    public async Task SetPasswordAsync(string loginUsername, string password, string? currentPassword)
    {
        EnsureNotKiosk("Changing the password");

        // Resolve the signed-in user first: this asserts an authenticated identity and gives the id
        // the credential is attached to. There is no path here that creates a user.
        var user = await GetCurrentUserAsync();

        await _passwordLoginService.SetPasswordAsync(user.Id, loginUsername, password, currentPassword);

        InvalidateCache();
    }

    public async Task<TotpEnrollment> BeginTotpEnrollmentAsync()
    {
        EnsureNotKiosk("Enrolling a second factor");

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
        EnsureNotKiosk("Enrolling a second factor");

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
        EnsureNotKiosk("Disabling the second factor");

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


    /// <summary>
    /// Refuses an account-security change while the session belongs to a kiosk tablet.
    /// </summary>
    /// <remarks>
    /// A kiosk session keeps the picked user's full authority over the WALL, but it must never be
    /// able to take over the ACCOUNT: whoever is standing at the tablet would still own the login
    /// long after the 30 minutes are up. The check lives here, next to the mutation, because these
    /// are called straight from interactive Blazor components inside the circuit, where no route
    /// middleware ever runs — hiding the buttons is not a gate.
    /// </remarks>
    private void EnsureNotKiosk(string action)
    {
        if (_kioskContext is { IsKiosk: true })
        {
            throw new KioskRestrictedException($"{action} is not available from a kiosk session.");
        }
    }
}
