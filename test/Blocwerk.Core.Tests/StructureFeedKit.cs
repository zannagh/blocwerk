// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>Contexts with the production feed mappers, and the rows they wrote, for the wall-structure mapper tests.</summary>
internal static class StructureFeedKit
{
    // Every field a structure payload may carry: ids, flags, enums, generation numbers, and an operation label.
    private static readonly HashSet<string> AllowedFields = ["sv", "generation", "panelId", "isDraft", "role", "reason", "ownerChanged"];

    public static FeedInterceptor Interceptor(IFeedSignal? signal = null) =>
        new(new FeedMapperRegistry(FeedMappers.Create()), signal ?? new RecordingFeedSignal(), NullLogger<FeedInterceptor>.Instance, TestFeedCommits.Unobserved());

    public static BlocwerkDbContext Context(WallTestHarness harness, IFeedSignal? signal = null) =>
        TestDb.Create(harness.DbContextFactory.ConnectionString, Interceptor(signal));

    /// <summary>The feed rows in write order, after checking that none carries more than ids.</summary>
    public static async Task<List<WallChange>> RowsAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        var rows = await db.WallChanges.AsNoTracking().OrderBy(c => c.Seq).ToListAsync();
        foreach (var row in rows.Where(r => r.DataJson is not null))
        {
            using var json = JsonDocument.Parse(row.DataJson!);
            var extra = json.RootElement.EnumerateObject().Select(p => p.Name).Where(n => !AllowedFields.Contains(n)).ToList();
            Assert.True(extra.Count == 0, $"{row.Kind} carries {string.Join(", ", extra)}");
        }

        return rows;
    }

    public static JsonElement Payload(WallChange row)
    {
        using var json = JsonDocument.Parse(row.DataJson ?? "null");
        return json.RootElement.Clone();
    }

    public static async Task<Boulder> SeedBoulderAsync(WallTestHarness harness, Guid holdId, bool isDraft = false)
    {
        await using var db = harness.CreateContext();
        var boulder = new Boulder { WallId = harness.WallId, Name = "Probe", CreatedByUserId = harness.Owner.Id, IsDraft = isDraft };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        await db.SaveChangesAsync();
        return boulder;
    }
}
