// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>A seeded wall whose contexts carry the feed interceptor with the production mappers, for the social mapper tests.</summary>
internal sealed class SocialFeedFixture : IDisposable
{
    public SocialFeedFixture(PostgresTestDatabase? postgres = null, IFeedSignal? signal = null)
    {
        Harness = new WallTestHarness(postgres);
        Signal = signal ?? new RecordingFeedSignal();
    }

    public WallTestHarness Harness { get; }

    public IFeedSignal Signal { get; }

    public Guid WallId => Harness.WallId;

    public Guid OwnerId => Harness.Owner.Id;

    /// <summary>A context with the feed interceptor, saving as <paramref name="actor"/>.</summary>
    public BlocwerkDbContext Context(Guid? actor = null) => Context(actor, NullLogger<FeedInterceptor>.Instance);

    /// <summary>A context with the feed interceptor logging to <paramref name="logger"/>, then <paramref name="more"/> interceptors.</summary>
    public BlocwerkDbContext Context(Guid? actor, ILogger<FeedInterceptor> logger, params IInterceptor[] more)
    {
        var db = TestDb.Create(
            Harness.DbContextFactory.ConnectionString,
            [new FeedInterceptor(new FeedMapperRegistry(FeedMappers.Create()), Signal, logger), .. more]);
        db.CurrentUserId = actor ?? Guid.Empty;
        return db;
    }

    public async Task<Guid> SeedAsync()
    {
        await Harness.SeedWallAsync(holdCount: 0);
        return await AddBoulderAsync();
    }

    /// <summary>Adds a boulder without the interceptor, so no row is written for it.</summary>
    public async Task<Guid> AddBoulderAsync(bool isDraft = false)
    {
        await using var db = Harness.CreateContext();
        var boulder = new Boulder { WallId = WallId, Name = "B" + Guid.NewGuid().ToString("N")[..6], CreatedByUserId = OwnerId, IsDraft = isDraft };
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    /// <summary>Adds <paramref name="entity"/> through the interceptor, in a context that does not track the boulder.</summary>
    public async Task AddAsync(object entity, Guid? actor = null)
    {
        await using var db = Context(actor);
        db.Add(entity);
        await db.SaveChangesAsync();
    }

    /// <summary>Loads the <typeparamref name="T"/> matching <paramref name="match"/>, changes it and saves it through the interceptor.</summary>
    public async Task ChangeAsync<T>(Func<T, bool> match, Action<T, BlocwerkDbContext> change, Guid? actor = null)
        where T : class
    {
        await using var db = Context(actor);
        var entity = (await db.Set<T>().IgnoreQueryFilters().ToListAsync()).Single(match);
        change(entity, db);
        await db.SaveChangesAsync();
    }

    public async Task<List<WallChange>> RowsAsync()
    {
        await using var db = Harness.CreateContext();
        return await db.WallChanges.AsNoTracking().OrderBy(c => c.Seq).ToListAsync();
    }

    public async Task ClearRowsAsync()
    {
        await using var db = Harness.CreateContext();
        db.WallChanges.RemoveRange(await db.WallChanges.ToListAsync());
        await db.SaveChangesAsync();
    }

    public void Dispose() => Harness.Dispose();
}
