using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="ILoginLockoutService"/> over the EF store. Each call owns its own short-lived DbContext so
/// the lockout state is always read/written against the authoritative row, independent of any cached or
/// AsNoTracking user the caller may be holding.
/// </summary>
public class LoginLockoutService : ILoginLockoutService
{
    // Policy: lock after this many consecutive failures, for this long. Small enough to stop an online
    // brute force of a 6-digit code (or a password) while staying a mild nuisance for a fat-fingering user.
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;

    public LoginLockoutService(IDbContextFactory<BlocwerkDbContext> dbContextFactory)
    {
        this.dbContextFactory = dbContextFactory;
    }

    public async Task<bool> IsLockedAsync(Guid userId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        return user?.LockoutUntil is { } until && until > DateTimeOffset.UtcNow;
    }

    public async Task RegisterFailureAsync(Guid userId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // The previous window has elapsed — start counting fresh before recording this failure.
        if (user.LockoutUntil is { } until && until <= now)
        {
            user.FailedAuthCount = 0;
            user.LockoutUntil = null;
        }

        user.FailedAuthCount++;
        if (user.FailedAuthCount >= MaxFailures)
        {
            user.LockoutUntil = now.Add(LockoutDuration);
        }

        await dbContext.SaveChangesAsync();
    }

    public async Task ResetAsync(Guid userId)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync();
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            return;
        }

        if (user.FailedAuthCount == 0 && user.LockoutUntil is null)
        {
            return;
        }

        user.FailedAuthCount = 0;
        user.LockoutUntil = null;
        await dbContext.SaveChangesAsync();
    }
}
