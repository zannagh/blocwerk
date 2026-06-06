using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Authentication.Services;

public class DevCurrentUserService : ICurrentUserService
{
    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;

    public DevCurrentUserService(IDbContextFactory<BlocwerkDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<User> GetCurrentUserAsync()
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Identifier == "dev__local");

        if (user != null)
        {
            return user;
        }

        user = new User
        {
            Identifier = "dev__local",
            DisplayName = "Dev User",
            Role = IdentityRole.Admin,
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
