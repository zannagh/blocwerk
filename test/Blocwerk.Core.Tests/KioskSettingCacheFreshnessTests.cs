// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall's kiosk settings panels (WallKioskAnonymousPanel, WallKioskShortcutPanel) read their flag from the
/// circuit's cached wall. When ANOTHER admin flips one, from another scope, this circuit's cached wall must show the
/// new value, both ways the app learns of a write: a save in this process (the synchronous own-write path every circuit
/// observes) and a save on another instance (only the feed delivery reaches the circuit).
/// </summary>
public class KioskSettingCacheFreshnessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnotherAdminsSaveInThisProcess_IsInThisCircuitsCachedWall(bool shortcuts)
    {
        await using var f = new OwnWriteFixture();
        await f.StartAsync();
        Assert.False(Flag(await f.Cache.GetWallAsync(f.WallId), shortcuts));

        await AsOtherAdminAsync(f.Harness, () => Set(f.Request(), f.WallId, shortcuts));

        Assert.Contains(f.Changes, c => c.Scope == WallCacheChangeScope.Wall && c.WallId == f.WallId);
        Assert.True(Flag(await f.Cache.GetWallAsync(f.WallId), shortcuts));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnotherAdminsSaveOnAnotherInstance_ReachesThisCircuitsCachedWallThroughTheFeed(bool shortcuts)
    {
        await using var f = new CircuitCacheFixture();
        await f.StartAsync();
        Assert.False(Flag(await f.Cache.GetWallAsync(f.WallId), shortcuts));

        // Another instance: the production interceptor and mappers write the feed rows and wake the hub, but its own
        // commits are observed by nobody here, so only the delivery can tell this circuit.
        var interceptor = new FeedInterceptor(
            new FeedMapperRegistry(FeedMappers.Create()), f.Signal, NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());
        var otherInstance = new OwnWriteServices(
            new InterceptedDbContextFactory(f.Harness.DbContextFactory.ConnectionString, [interceptor]), f.Harness);

        await AsOtherAdminAsync(f.Harness, () => Set(otherInstance, f.WallId, shortcuts));

        await CircuitCacheFixture.WaitUntilAsync(() => f.Changes.Any(c => c.Scope == WallCacheChangeScope.Wall && c.WallId == f.WallId));
        Assert.True(Flag(await f.Cache.GetWallAsync(f.WallId), shortcuts));
    }

    private static bool Flag(Wall? wall, bool shortcuts)
    {
        Assert.NotNull(wall);
        return shortcuts ? wall.AllowKioskKeyboardShortcuts : wall.AllowAnonymousKioskSetting;
    }

    private static Task Set(OwnWriteServices services, Guid wallId, bool shortcuts) =>
        shortcuts
            ? services.Walls.SetKioskKeyboardShortcutsAsync(wallId, true)
            : services.Walls.SetAnonymousKioskSettingAsync(wallId, true);

    // A second admin of the wall makes the change; the circuit's own reads stay the owner's.
    private static async Task AsOtherAdminAsync(WallTestHarness harness, Func<Task> write)
    {
        var admin = new User { Identifier = "admin2@test", DisplayName = "Second admin" };
        await using (var db = harness.CreateContext())
        {
            db.Users.Add(admin);
            db.WallMembers.Add(new WallMember { WallId = harness.WallId, UserId = admin.Id, Role = WallRole.Admin });
            await db.SaveChangesAsync();
        }

        var owner = harness.ActingUser;
        harness.ActingUser = admin;
        try
        {
            await write();
        }
        finally
        {
            harness.ActingUser = owner;
        }
    }
}
