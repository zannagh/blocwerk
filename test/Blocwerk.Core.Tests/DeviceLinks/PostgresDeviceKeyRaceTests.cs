// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.DeviceLinks;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.DeviceLinks;

/// <summary>
/// Device keys minted at the same moment, on a real PostgreSQL with one connection per link: the five-key cap and the
/// one-key-per-device rule still hold, because each mint locks the user's row first.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresDeviceKeyRaceTests
{
    private const int Rounds = 5;

    [PostgresFact]
    public async Task ConcurrentLinksOfDifferentDevices_KeepTheCap()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync();
        for (var round = 0; round < Rounds; round++)
        {
            var names = Enumerable.Range(0, DeviceLinkPolicy.MaxLiveDeviceKeys + 2).Select(i => $"iPhone: {round}-{i}");

            await MintAtOnceAsync(h, names);

            Assert.Equal(DeviceLinkPolicy.MaxLiveDeviceKeys, (await LiveNamesAsync(h)).Count);
        }
    }

    [PostgresFact]
    public async Task ConcurrentLinksOfTheSameDevice_LeaveOneLiveKey()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync();
        for (var round = 0; round < Rounds; round++)
        {
            await MintAtOnceAsync(h, Enumerable.Repeat("iPhone: Same", 4));

            Assert.Equal(["iPhone: Same"], await LiveNamesAsync(h));
        }
    }

    private static async Task MintAtOnceAsync(WallTestHarness h, IEnumerable<string> names)
    {
        using var start = new ManualResetEventSlim();
        var mints = names.Select(name => Task.Run(async () =>
        {
            start.Wait();
            await h.ApiKeyService.CreateDeviceKeyAsync(h.Owner.Id, name);
        })).ToList();
        start.Set();
        await Task.WhenAll(mints);
    }

    private static async Task<List<string>> LiveNamesAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.ApiKeys.IgnoreQueryFilters()
            .Where(k => k.UserId == h.Owner.Id && k.IsDevice && k.RevokedAt == null)
            .Select(k => k.Name)
            .ToListAsync();
    }
}
