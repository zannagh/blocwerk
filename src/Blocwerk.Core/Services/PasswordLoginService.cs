using System.Text.RegularExpressions;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
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

    public PasswordLoginService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, IPasswordService passwordService)
    {
        this.dbContextFactory = dbContextFactory;
        this.passwordService = passwordService;
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
            .FirstOrDefaultAsync(u => u.LoginUsername != null
                                      && u.PasswordHash != null
                                      && u.LoginUsername.ToLower() == normalized);
    }

    private static bool IsValidUsername(string username)
    {
        return username.Length is >= MinUsernameLength and <= MaxUsernameLength
               && UsernamePattern().IsMatch(username);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex UsernamePattern();
}
