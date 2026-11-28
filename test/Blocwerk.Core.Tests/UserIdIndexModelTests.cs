// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The replay keys' unique indexes on (UserId, ClientRequestId) are partial (only rows with a key), so they cannot serve
/// a lookup of a user's attempts or comments. Each table keeps its own plain, unfiltered UserId index beside them; EF
/// would otherwise drop it as "covered" by the composite one.
/// </summary>
public sealed class UserIdIndexModelTests
{
    [Theory]
    [InlineData(typeof(Attempt))]
    [InlineData(typeof(BoulderComment))]
    public void TheTable_KeepsAPlainUserIdIndex_BesideItsPartialReplayIndex(Type entity)
    {
        using var h = new WallTestHarness();
        using var db = h.CreateContext();
        var indexes = db.Model.FindEntityType(entity)!.GetIndexes().ToList();

        Assert.Contains(indexes, i => i.Properties.Select(p => p.Name).SequenceEqual(["UserId"]) && i.GetFilter() is null);
        var replay = Assert.Single(indexes, i => i.Properties.Select(p => p.Name).SequenceEqual(["UserId", "ClientRequestId"]));
        Assert.True(replay.IsUnique);
        Assert.NotNull(replay.GetFilter());
    }

    [Fact]
    public void TheMigrations_EndWithBothIndexesInTheSnapshot()
    {
        var snapshot = File.ReadAllText(Path.Combine(RepoRoot(), "src/Blocwerk.Core/Migrations/BlocwerkDbContextModelSnapshot.cs"));

        foreach (var entity in new[] { "Attempt", "BoulderComment" })
        {
            var start = snapshot.IndexOf($"modelBuilder.Entity(\"Blocwerk.Core.Entities.{entity}\", b =>", StringComparison.Ordinal);
            var end = snapshot.IndexOf("modelBuilder.Entity(", start + 1, StringComparison.Ordinal);
            var block = snapshot[start..end];
            Assert.Contains("b.HasIndex(\"UserId\");", block);
            Assert.Contains("b.HasIndex(\"UserId\", \"ClientRequestId\")", block);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Blocwerk.slnx not found above the test binaries.");
    }
}
