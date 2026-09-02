using Blocwerk.Authentication.Services;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Maintenance;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Avatar normalisation. The operation replaces stored bytes irreversibly, so what is asserted here
/// is the blast radius: it must leave a normal avatar alone, it must produce exactly what a fresh
/// upload would produce, and a dry run must change nothing at all.
/// </summary>
public class AvatarNormalizationTests
{
    [Fact]
    public async Task AnAlreadySmallAvatarIsLeftAlone()
    {
        using var harness = new WallTestHarness();
        var (small, type) = AvatarImageEncoder.Scale(TestImages.Noise(900, 900));
        Assert.True(small.Length <= AvatarNormalizer.MaxStoredBytes);

        var userId = await SeedAvatarAsync(harness, small, type);

        var summary = await Normalizer(harness).NormalizeAsync(dryRun: false, Log(), CancellationToken.None);

        Assert.Equal(0, summary.Examined);
        Assert.Equal(0, summary.Rewritten);
        Assert.Equal(small, await StoredAsync(harness, userId));
    }

    /// <summary>
    /// The whole point of reusing <see cref="AvatarImageEncoder"/>: a normalised legacy avatar and
    /// a freshly uploaded one are the same bytes under the same content type.
    /// </summary>
    [Fact]
    public async Task AnOversizedAvatarBecomesExactlyWhatAFreshUploadWouldStore()
    {
        using var harness = new WallTestHarness();
        var legacy = TestImages.Noise(3000, 3000, SKEncodedImageFormat.Jpeg, quality: 100);
        Assert.True(legacy.Length > AvatarNormalizer.MaxStoredBytes, "the fixture must exceed the threshold");

        var userId = await SeedAvatarAsync(harness, legacy, "image/jpeg");
        var (expected, expectedType) = AvatarImageEncoder.Scale(legacy);

        var summary = await Normalizer(harness).NormalizeAsync(dryRun: false, Log(), CancellationToken.None);

        Assert.Equal(1, summary.Examined);
        Assert.Equal(1, summary.Rewritten);
        Assert.Equal(0, summary.Failed);
        Assert.True(summary.BytesAfter < summary.BytesBefore);

        var (stored, storedType) = await StoredWithTypeAsync(harness, userId);
        Assert.Equal(expected, stored);
        Assert.Equal(expectedType, storedType);

        using var decoded = SKBitmap.Decode(stored);
        Assert.Equal(AvatarImageEncoder.MaxEdge, Math.Max(decoded.Width, decoded.Height));
    }

    [Fact]
    public async Task ADryRunReportsTheChangeAndWritesNothing()
    {
        using var harness = new WallTestHarness();
        var legacy = TestImages.Noise(3000, 3000, SKEncodedImageFormat.Jpeg, quality: 100);
        var userId = await SeedAvatarAsync(harness, legacy, "image/jpeg");

        var summary = await Normalizer(harness).NormalizeAsync(dryRun: true, Log(), CancellationToken.None);

        Assert.True(summary.DryRun);
        Assert.Equal(1, summary.Rewritten);
        Assert.True(summary.BytesAfter < summary.BytesBefore, "a dry run still reports what it would save");

        var (stored, storedType) = await StoredWithTypeAsync(harness, userId);
        Assert.Equal(legacy, stored);
        Assert.Equal("image/jpeg", storedType);
    }

    /// <summary>The log is the only record of what was overwritten, so it has to carry the sizes.</summary>
    [Fact]
    public async Task EveryTouchedAvatarIsLoggedWithItsBeforeAndAfter()
    {
        using var harness = new WallTestHarness();
        var legacy = TestImages.Noise(3000, 3000, SKEncodedImageFormat.Jpeg, quality: 100);
        var userId = await SeedAvatarAsync(harness, legacy, "image/jpeg");

        var lines = new List<string>();
        await Normalizer(harness).NormalizeAsync(dryRun: false, new MaintenanceJobLog(_ => { }, lines.Add), CancellationToken.None);

        var line = Assert.Single(lines, l => l.Contains(userId.ToString(), StringComparison.Ordinal));
        Assert.Contains("REWROTE", line, StringComparison.Ordinal);
        Assert.Contains($"{legacy.Length} B image/jpeg 3000x3000", line, StringComparison.Ordinal);
        Assert.Contains($"{AvatarImageEncoder.MaxEdge}x{AvatarImageEncoder.MaxEdge}", line, StringComparison.Ordinal);
    }

    private static AvatarNormalizer Normalizer(WallTestHarness harness) =>
        new(harness.DbContextFactory, NullLogger<AvatarNormalizer>.Instance);

    private static MaintenanceJobLog Log() => new(_ => { }, _ => { });

    private static async Task<Guid> SeedAvatarAsync(WallTestHarness harness, byte[] avatar, string contentType)
    {
        await using var db = harness.CreateContext();
        var user = new User
        {
            Identifier = "avatar@test",
            DisplayName = "Avatar",
            AvatarImage = avatar,
            AvatarContentType = contentType,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<byte[]?> StoredAsync(WallTestHarness harness, Guid userId) =>
        (await StoredWithTypeAsync(harness, userId)).Image;

    private static async Task<(byte[]? Image, string? ContentType)> StoredWithTypeAsync(
        WallTestHarness harness, Guid userId)
    {
        await using var db = harness.CreateContext();
        var user = db.Users.Single(u => u.Id == userId);
        await Task.CompletedTask;
        return (user.AvatarImage, user.AvatarContentType);
    }
}
