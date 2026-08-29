using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Development-only convenience: makes the configured dev user (<c>BLOCWERK_DEV_USER</c>, the same
/// identifier the authentication project's dev middleware signs in as) a
/// <see cref="WallRole.Admin"/> member of every wall, so local testing sees all walls and can
/// administer them without hand-seeding membership. Never wired outside Development, and it only
/// adds membership rows — it changes no production authorization logic.
/// </summary>
public static class DevWallAdminSeeder
{
    public static async Task SeedIfNeededAsync(
        IDbContextFactory<BlocwerkDbContext> factory,
        ILogger logger)
    {
        var identifier = Environment.GetEnvironmentVariable("BLOCWERK_DEV_USER");
        if (string.IsNullOrWhiteSpace(identifier))
        {
            // No dev user configured — real OAuth is in use even in Development, so seed nothing.
            return;
        }

        await using var db = await factory.CreateDbContextAsync();
        db.CurrentUserId = Guid.Empty;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Identifier == identifier);
        if (user is null)
        {
            user = new User
            {
                Identifier = identifier,
                DisplayName = identifier.Split("__").FirstOrDefault() ?? identifier,
                Role = IdentityRole.Admin,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            logger.LogInformation("Dev wall-admin seed: created dev user {Identifier}.", identifier);
        }

        // Global admin so any admin-only global UI is visible too.
        if (user.Role != IdentityRole.Admin)
        {
            user.Role = IdentityRole.Admin;
        }

        var wallIds = await db.Walls.IgnoreQueryFilters().Select(w => w.Id).ToListAsync();
        var existing = await db.WallMembers
            .Where(m => m.UserId == user.Id)
            .ToDictionaryAsync(m => m.WallId);

        var added = 0;
        foreach (var wallId in wallIds)
        {
            if (existing.TryGetValue(wallId, out var member))
            {
                if (member.Role != WallRole.Admin)
                {
                    member.Role = WallRole.Admin;
                }

                continue;
            }

            db.WallMembers.Add(new WallMember
            {
                UserId = user.Id,
                WallId = wallId,
                Role = WallRole.Admin,
            });
            added++;
        }

        await db.SaveChangesAsync();
        logger.LogInformation(
            "Dev wall-admin seed: {Identifier} is now admin on {Total} wall(s) ({Added} newly added).",
            identifier,
            wallIds.Count,
            added);
    }
}
