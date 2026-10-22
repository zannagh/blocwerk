// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Reads the feed rows the bulk sites appended, after checking that none carries more than ids, enums and flags.</summary>
internal static class FeedBulkKit
{
    private static readonly HashSet<string> AllowedFields =
        ["sv", "generation", "panelId", "isDraft", "role", "reason", "boulderId", "encodingStatus", "hasHls", "type", "ownerChanged"];

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

    public static async Task ClearAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        await db.WallChanges.ExecuteDeleteAsync();
    }

    public static string? Field(WallChange row, string name)
    {
        using var json = JsonDocument.Parse(row.DataJson ?? "{}");
        if (!json.RootElement.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }
}
