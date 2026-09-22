using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services.TopLogger;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The unmapped-grade summary is built by a grouped database query, so it has to actually execute
/// against a relational provider — an in-memory list would hide an untranslatable projection.
/// </summary>
public class TopLoggerUnmappedGradeTests
{
    [Fact]
    public async Task GetUnmappedGrades_GroupsCountsAndSamples_AgainstARelationalProvider()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        await SeedAscentsAsync(harness,
            ("7A", "Zulu", true),
            ("7A", "Alpha", true),
            ("6B", "Bravo", true),
            (null, "Charlie", true),
            (string.Empty, "Delta", true),
            ("6C", "Mapped", false));

        var service = CreateService(harness);

        var grades = await service.GetUnmappedGradesAsync(harness.Owner.Id);

        Assert.Equal(3, grades.Count);

        // Ordered by count descending: 7A (2), then the empty bucket (2), then 6B (1) — ties are
        // stable enough to assert only on the head and on membership.
        Assert.Equal(2, grades[0].Count);

        var byGrade = grades.ToDictionary(g => g.RawGrade);
        Assert.Equal(2, byGrade["7A"].Count);
        Assert.Equal("Alpha", byGrade["7A"].SampleClimbName);

        // Null and empty collapse into one "" bucket.
        Assert.Equal(2, byGrade[string.Empty].Count);
        Assert.Equal("Charlie", byGrade[string.Empty].SampleClimbName);

        Assert.Equal(1, byGrade["6B"].Count);
        Assert.Equal("Bravo", byGrade["6B"].SampleClimbName);

        // A mapped ascent never shows up.
        Assert.DoesNotContain(grades, g => g.RawGrade == "6C");
    }

    [Fact]
    public async Task GetUnmappedGrades_ReturnsEmpty_WhenNothingNeedsMapping()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync();

        var service = CreateService(harness);

        var grades = await service.GetUnmappedGradesAsync(harness.Owner.Id);

        Assert.Empty(grades);
    }

    private static TopLoggerImportService CreateService(WallTestHarness harness) =>
        new(
            harness.DbContextFactory,
            Substitute.For<ITopLoggerApiClient>(),
            Substitute.For<ITopLoggerTokenStore>(),
            NullLogger<TopLoggerImportService>.Instance);

    private static async Task SeedAscentsAsync(
        WallTestHarness harness, params (string? RawGrade, string ClimbName, bool NeedsMapping)[] ascents)
    {
        await using var db = harness.CreateContext();
        var index = 0;
        foreach (var (rawGrade, climbName, needsMapping) in ascents)
        {
            db.ExternalAscents.Add(new ExternalAscent
            {
                UserId = harness.Owner.Id,
                Source = ExternalSource.TopLogger,
                ExternalId = $"tick-{index++}",
                ClimbName = climbName,
                RawGrade = rawGrade,
                NeedsGradeMapping = needsMapping,
                LoggedAt = DateTimeOffset.UtcNow,
                Type = AttemptType.Send,
                Ticked = true,
            });
        }

        await db.SaveChangesAsync();
    }
}
