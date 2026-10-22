// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The source-scan guard (design §5.3, B2): every <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> on an entity the feed covers
/// (mapped, or reviewed-excluded in <see cref="FeedCoverage"/>, or user-scoped: <see cref="FeedBulkSites.UserScoped"/>) bypasses the feed interceptor, so each one is classified in
/// <see cref="FeedBulkSites"/>: it appends its rows itself, or is excluded with a reason. A new call, a moved one or one
/// on another entity fails here until it is classified.
/// </summary>
public sealed class FeedBulkSiteGuardTests
{
    [Fact]
    public void EveryBulkWriteOnAFeedEntity_IsClassified()
    {
        var found = Found();
        var unclassified = found
            .Where(f => !FeedBulkSites.All.Any(s => s.File == f.Key.File && s.Entity == f.Key.Entity && s.Count == f.Value))
            .Select(f => $"{f.Key.File} {f.Key.Entity?.Name ?? "(unresolved)"} x{f.Value}")
            .Order()
            .ToList();
        var message = "Classify these ExecuteUpdate/ExecuteDelete calls in FeedBulkSites (append feed rows through FeedBulkWrite, "
            + "or exclude with a reason), or fix the count:\n" + string.Join("\n", unclassified);
        Assert.True(unclassified.Count == 0, message);
    }

    [Fact]
    public void NoClassificationIsStale()
    {
        var found = Found();
        var stale = FeedBulkSites.All
            .Where(s => !found.TryGetValue((s.File, s.Entity), out var count) || count != s.Count)
            .Select(s => $"{s.File} {s.Entity?.Name ?? "(unresolved)"} x{s.Count}")
            .ToList();
        Assert.True(stale.Count == 0, "Classified but not found (remove or fix the entry):\n" + string.Join("\n", stale));
    }

    [Fact]
    public void EveryHandledSite_GoesThroughTheFeed_AndEveryExclusionSaysWhy()
    {
        var src = Path.Combine(FeedBulkSiteScanner.RepoRoot(), "src");
        foreach (var site in FeedBulkSites.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(site.Reason), site.File);
            if (site.Handling == FeedBulkHandling.Excluded)
            {
                continue;
            }

            // The rows may be appended from a sibling partial of the same class (AccountDeletionService.cs for .Erase.cs).
            var stem = Path.GetFileName(site.File).Split('.')[0];
            var dir = Path.Combine(src, Path.GetDirectoryName(site.File)!);
            var partials = Directory.EnumerateFiles(dir, stem + "*.cs").Select(File.ReadAllText);
            Assert.True(partials.Any(t => FeedBulkSites.Markers.Any(t.Contains)), $"{site.File} is classified {site.Handling} but never calls the feed");
        }
    }

    [Fact]
    public void TheScanner_ReadsTheEntity_OfADirectALocalATypedAndAReturnedQuery()
    {
        const string source = """
            await db.Holds.Where(h => h.Id == id).ExecuteUpdateAsync(s => s.SetProperty(h => h.X, 1));
            var mine = db.WallImages.Where(i => i.WallId == wall);
            await FeedBulkWrite.RunAsync(db, "x", () => mine.ExecuteDeleteAsync(ct), _ => [], logger, ct);
            IQueryable<BetaVideo> clips = Pick();
            // await db.Boulders.ExecuteDeleteAsync(); a comment is not a call
            await clips.Where(v => v.HasHls).ExecuteDeleteAsync();
            await rows(db).ExecuteUpdateAsync(s => s.SetProperty(x => x.Photo, clean));
            await Narrow(ids).ExecuteDeleteAsync();
            private static IQueryable<Hold> Narrow(List<Guid> ids) => null!;
            """;

        var calls = FeedBulkSiteScanner.ScanText("probe.cs", source).Select(c => c.Entity).ToList();

        Assert.Equal([typeof(Hold), typeof(WallImage), typeof(BetaVideo), null, typeof(Hold)], calls);
    }

    private static Dictionary<(string File, Type? Entity), int> Found()
    {
        var covered = new FeedMapperRegistry(FeedMappers.Create()).MappedTypes.Concat(FeedCoverage.ReviewedExcluded.Keys).Concat(FeedBulkSites.UserScoped).ToHashSet();
        return FeedBulkSiteScanner.Scan()
            .Where(c => c.Entity is null || covered.Contains(c.Entity))
            .GroupBy(c => (c.File, c.Entity))
            .ToDictionary(g => g.Key, g => g.Count());
    }
}
