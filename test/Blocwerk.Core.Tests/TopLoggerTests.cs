using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.TopLogger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

public class TopLoggerTests
{
    [Theory]
    [InlineData("6C", "6C")]      // already a Font label
    [InlineData("7A+", "7A+")]
    [InlineData("V5", "6C")]      // V-scale
    [InlineData("6.0", "6A")]     // decimal encoding: fraction is the sub-grade in sixths
    [InlineData("6.5", "6B+")]
    [InlineData("6.67", "6C")]
    [InlineData("7", "7A")]
    public void GradeMapper_MapsKnownRepresentations(string raw, string expected)
    {
        Assert.Equal(expected, TopLoggerGradeMapper.ToFontGrade(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-grade")]
    public void GradeMapper_ReturnsNullForUnmappable(string? raw)
    {
        Assert.Null(TopLoggerGradeMapper.ToFontGrade(raw));
    }

    [Fact]
    public void TokenProtector_RoundTripsWhenKeyConfigured()
    {
        var protector = new TokenProtector(SettingsWithKey("a-secret-passphrase"));

        Assert.True(protector.IsConfigured);
        const string token = "tl_secret_token_value";
        var cipher = protector.Protect(token);

        Assert.NotEqual(token, cipher);
        Assert.Equal(token, protector.Unprotect(cipher));
    }

    [Fact]
    public void TokenProtector_DisabledWithoutKey()
    {
        var protector = new TokenProtector(SettingsWithKey(null));

        Assert.False(protector.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => protector.Protect("x"));
    }

    [Fact]
    public async Task Sync_StoresAscents_ClustersIntoActivities_AndDedupes()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();
        var protector = new TokenProtector(SettingsWithKey("key"));

        var baseTime = new DateTimeOffset(2026, 2, 1, 18, 0, 0, TimeSpan.Zero);
        var client = Substitute.For<ITopLoggerClient>();
        client.GetAscentsAsync(Arg.Any<TopLoggerCredentials>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(new List<TopLoggerAscentDto>
            {
                new("a1", "Slab", "Gym", "6.67", null, AttemptType.Send, baseTime),
                new("a2", "Crimp", "Gym", "6.5", null, AttemptType.Flash, baseTime.AddHours(1)),
                new("a3", "Roof", "Gym", "7.0", null, AttemptType.Send, baseTime.AddHours(6)), // new session
            });

        var service = new TopLoggerService(
            harness.DbContextFactory, harness.CurrentUser, client, protector, NullLogger<TopLoggerService>.Instance);

        TopLoggerConnection connection;
        await using (var db = harness.CreateContext())
        {
            connection = new TopLoggerConnection
            {
                UserId = harness.Owner.Id,
                Email = "me@test",
                TokenEncrypted = protector.Protect("tok"),
                UserUid = "1",
                Backend = TopLoggerBackend.Legacy,
            };
            db.TopLoggerConnections.Add(connection);
            await db.SaveChangesAsync();

            var imported = await service.SyncConnectionAsync(db, connection, CancellationToken.None);
            Assert.Equal(3, imported);
        }

        await using (var db = harness.CreateContext())
        {
            var ascents = await db.ExternalAscents.Where(a => a.UserId == harness.Owner.Id).ToListAsync();
            Assert.Equal(3, ascents.Count);
            Assert.All(ascents, a => Assert.NotNull(a.ActivityId));            // clustered
            Assert.Equal("6C", ascents.Single(a => a.ExternalId == "a1").Grade); // grade mapped
            Assert.Equal(2, ascents.Select(a => a.ActivityId).Distinct().Count()); // 4h gap → two activities
        }

        // Re-sync imports nothing (dedupe by external id).
        await using (var db = harness.CreateContext())
        {
            var again = await service.SyncConnectionAsync(db, connection, CancellationToken.None);
            Assert.Equal(0, again);
            Assert.Equal(3, await db.ExternalAscents.CountAsync(a => a.UserId == harness.Owner.Id));
        }
    }

    private static BlocwerkSettings SettingsWithKey(string? key)
    {
        var items = new Dictionary<string, string?>();
        if (key is not null)
        {
            items["Blocwerk:EncryptionKey"] = key;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(items).Build();
        return new BlocwerkSettings(config);
    }
}
