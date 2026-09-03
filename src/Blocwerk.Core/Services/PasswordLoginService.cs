using System.Text.RegularExpressions;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IPasswordLoginService"/> over the EF store. See the interface for the no-signup /
/// no-enumeration guarantees; this type only ever updates an existing row or reads.
/// </summary>
public partial class PasswordLoginService : IPasswordLoginService
{
    private const int MinUsernameLength = 3;
    private const int MaxUsernameLength = 64;
    private const int MinPasswordLength = 8;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly IPasswordService passwordService;
    private readonly IKioskContext? kioskContext;

    /// <summary>Creates the service.</summary>
    /// <remarks>
    /// <c>kioskContext</c> is optional: hosts with no HTTP layer never register one, which simply
    /// means "never a kiosk". See <see cref="KioskGuard"/> for why the stamped database context is
    /// read as a second source.
    /// </remarks>
    public PasswordLoginService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        IPasswordService passwordService,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.passwordService = passwordService;
        this.kioskContext = kioskContext;
    }

    public async Task SetPasswordAsync(Guid userId, string loginUsername, string password, string? currentPassword)
    {
        var username = (loginUsername ?? string.Empty).Trim();
        if (!IsValidUsername(username))
        {
            throw new InvalidOperationException(
                "Choose a username of 3–64 characters using letters, digits, dot, underscore or hyphen.");
        }

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            throw new InvalidOperationException($"Password must be at least {MinPasswordLength} characters.");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        var dbUser = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId)
                     ?? throw new InvalidOperationException("User not found.");

        // Step-up re-auth: changing an EXISTING password requires proving the current one, so a hijacked
        // session (or a walked-away device) can't silently take over the password credential. A first-time
        // set (no existing password, OAuth-authenticated) needs no current password.
        if (dbUser.HasPassword)
        {
            if (string.IsNullOrEmpty(currentPassword) || !passwordService.Verify(dbUser.PasswordHash!, currentPassword))
            {
                throw new InvalidOperationException("Your current password is incorrect.");
            }
        }

        var normalized = username.ToLowerInvariant();

        // Case-insensitive uniqueness across OTHER users. The DB now carries a case-insensitive functional
        // unique index (lower("LoginUsername")) as the real backstop against a TOCTOU race; this app-level
        // check stays only to surface a friendly error before the DB rejects the insert.
        bool taken = await dbContext.Users
            .AnyAsync(u => u.Id != userId
                           && u.LoginUsername != null
                           && u.LoginUsername.ToLower() == normalized);
        if (taken)
        {
            throw new InvalidOperationException("That username is already taken. Please choose another.");
        }

        dbUser.LoginUsername = username;
        dbUser.PasswordHash = passwordService.Hash(password);
        await dbContext.SaveChangesAsync();
    }

    public async Task<User?> FindByLoginUsernameAsync(string loginUsername)
    {
        var username = (loginUsername ?? string.Empty).Trim();
        if (username.Length == 0)
        {
            return null;
        }

        var normalized = username.ToLowerInvariant();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.DeletedAt == null
                                      && u.LoginUsername != null
                                      && u.PasswordHash != null
                                      && u.LoginUsername.ToLower() == normalized);
    }

    public async Task<bool> IsUsernameAvailableAsync(string loginUsername)
    {
        var username = (loginUsername ?? string.Empty).Trim();
        if (!IsValidUsername(username))
        {
            return false;
        }

        var normalized = username.ToLowerInvariant();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        bool taken = await dbContext.Users
            .AnyAsync(u => u.LoginUsername != null && u.LoginUsername.ToLower() == normalized);
        return !taken;
    }

    public async Task<User?> FindByEmailAsync(string email)
    {
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return null;
        }

        // Email is always stored normalized (lower-cased), so a plain equality is already case-insensitive.
        // Only a CONFIRMED email may match — an unverified/absent address must look like "no such account".
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        return await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.DeletedAt == null
                                      && u.Email != null
                                      && u.EmailVerified
                                      && u.Email == normalized);
    }

    public async Task<LocalUserCreateResult> CreateLocalUserAsync(string loginUsername, string password, string email)
    {
        // An account created at the tablet is the half of the share-link escalation that gives the
        // attacker somewhere to redeem it. Refused before any validation runs, so nothing about the
        // response distinguishes a kiosk refusal from a malformed request.
        KioskGuard.EnsureNotKiosk(kioskContext, "Creating an account");

        var username = (loginUsername ?? string.Empty).Trim();
        if (!IsValidUsername(username))
        {
            return new LocalUserCreateResult(LocalUserCreateStatus.Invalid);
        }

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            return new LocalUserCreateResult(LocalUserCreateStatus.Invalid);
        }

        var normalizedEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedEmail.Length == 0)
        {
            return new LocalUserCreateResult(LocalUserCreateStatus.Invalid);
        }

        var normalizedUsername = username.ToLowerInvariant();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        // App-level pre-checks for a friendly message. The DB unique indexes — lower("LoginUsername") and
        // the normalized Email — are the real race-safe backstop; a lost race surfaces as the
        // DbUpdateException caught below, so two concurrent signups for the same name can't both win.
        if (await dbContext.Users
                .AnyAsync(u => u.LoginUsername != null && u.LoginUsername.ToLower() == normalizedUsername))
        {
            return new LocalUserCreateResult(LocalUserCreateStatus.UsernameTaken);
        }

        if (await dbContext.Users.AnyAsync(u => u.Email == normalizedEmail))
        {
            return new LocalUserCreateResult(LocalUserCreateStatus.EmailTaken);
        }

        var user = new User
        {
            // Synthesized unique identifier for a local (non-OAuth) account. "local__{guid}" keeps the
            // "{name}__{authid}" shape the rest of the system splits on, without ever colliding with a real
            // provider subject. The chosen name lives in LoginUsername/DisplayName, not the identifier.
            Identifier = $"local__{Guid.NewGuid():N}",
            DisplayName = username,
            LoginUsername = username,
            PasswordHash = passwordService.Hash(password),
            Email = normalizedEmail,
            EmailVerified = true,
            Role = IdentityRole.User,

            // No OAuth UserIdentity for a local signup: the Identities collection stays empty. Resolution
            // by the "uid" claim (stamped at sign-in) never needs an identity row.
        };

        dbContext.Users.Add(user);

        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Lost the uniqueness race between the pre-check and the insert. Re-read on a fresh context to
            // report which constraint fired so the UI can steer the user correctly.
            await using var probe = await dbContextFactory.CreateDbContextAsync();
            if (await probe.Users.AnyAsync(u => u.Email == normalizedEmail))
            {
                return new LocalUserCreateResult(LocalUserCreateStatus.EmailTaken);
            }

            return new LocalUserCreateResult(LocalUserCreateStatus.UsernameTaken);
        }

        return new LocalUserCreateResult(LocalUserCreateStatus.Created, user);
    }

    public async Task ResetPasswordAsync(Guid userId, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinPasswordLength)
        {
            throw new InvalidOperationException($"Password must be at least {MinPasswordLength} characters.");
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync();

        // This is the SECOND way to write a password hash, and it does not go through
        // CurrentUserService.SetPasswordAsync — so the EnsureNotKiosk there never covered it. A
        // password set from a public tablet outlives the session by definition, which is the whole
        // reason account-security changes are refused on a kiosk in the first place.
        KioskGuard.EnsureNotKiosk(kioskContext, dbContext, "Resetting a password");

        var dbUser = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId)
                     ?? throw new InvalidOperationException("User not found.");

        // No step-up here: the verified reset code is itself the proof of control. Only the password hash
        // changes — the lockout counters are left to the login lockout service, unchanged.
        dbUser.PasswordHash = passwordService.Hash(newPassword);
        await dbContext.SaveChangesAsync();
    }

    private static bool IsValidUsername(string username)
    {
        return username.Length is >= MinUsernameLength and <= MaxUsernameLength
               && UsernamePattern().IsMatch(username);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex UsernamePattern();
}
