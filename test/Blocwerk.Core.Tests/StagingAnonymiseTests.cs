// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>tools/staging/anonymise.sql</c> against the real schema: a seeded user, API key and push subscription come out
/// anonymised or cleared, what staging keeps survives, and a second run changes nothing.
/// </summary>
[Trait("Db", "Postgres")]
public class StagingAnonymiseTests
{
    [PostgresFact]
    public async Task Script_AnonymisesUsers_ClearsAccess_AndIsIdempotent()
    {
        using var database = PostgresTestDatabase.Create();
        var (userId, wallId) = await SeedAsync(database);

        await RunScriptAsync(database);
        await RunScriptAsync(database);

        await using var db = database.CreateContext();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        Assert.Equal($"user-{userId}@staging.invalid", user.Email);
        Assert.Equal($"user-{userId}@staging.invalid", user.LoginUsername);
        Assert.Equal($"staging__{userId}", user.Identifier);
        Assert.Equal("hash", user.PasswordHash);
        Assert.Null(user.TotpSecretProtected);
        Assert.False(user.TotpEnabled);
        Assert.Equal("Climber", user.DisplayName);
        Assert.Empty(await db.ApiKeys.ToListAsync());
        Assert.Empty(await db.PushSubscriptions.ToListAsync());
        Assert.Empty(await db.UserIdentities.ToListAsync());
        Assert.Null((await db.Walls.IgnoreQueryFilters().SingleAsync(w => w.Id == wallId)).ShareToken);
        Assert.Contains(await db.Users.IgnoreQueryFilters().ToListAsync(), u => u.Identifier == "system:ghost");
    }

    private static async Task<(Guid UserId, Guid WallId)> SeedAsync(PostgresTestDatabase database)
    {
        await using var db = database.CreateContext();
        var user = new User
        {
            Identifier = "github__12345",
            DisplayName = "Climber",
            Email = "real@example.com",
            LoginUsername = "real@example.com",
            PasswordHash = "hash",
            TotpSecretProtected = "secret",
            TotpEnabled = true,
        };
        var wall = new Wall { Name = "Home", OwnerId = user.Id, ShareToken = "share-me" };
        db.Users.Add(user);
        db.Walls.Add(wall);
        db.UserIdentities.Add(new UserIdentity { UserId = user.Id, Provider = "github", ProviderUserId = "12345" });
        db.ApiKeys.Add(new ApiKey { Name = "mine", UserId = user.Id, KeyHash = "h", Prefix = "bwk_abcd" });
        db.PushSubscriptions.Add(new PushSubscription { UserId = user.Id, Endpoint = "https://push.example/1", P256dh = "p", Auth = "a" });
        await db.SaveChangesAsync();
        return (user.Id, wall.Id);
    }

    private static async Task RunScriptAsync(PostgresTestDatabase database)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(ScriptPath()), connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string ScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "staging", "anonymise.sql")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new FileNotFoundException("tools/staging/anonymise.sql"), "tools", "staging", "anonymise.sql");
    }
}
