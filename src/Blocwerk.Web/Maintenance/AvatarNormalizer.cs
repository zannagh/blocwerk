using Blocwerk.Authentication.Services;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Web.Maintenance;

/// <summary>
/// Re-encodes avatars that were stored before the upload path started scaling them, through the
/// current pipeline (<see cref="AvatarImageEncoder"/>) so the result is byte-for-byte what a fresh
/// upload of the same picture would produce today.
/// </summary>
/// <remarks>
/// DESTRUCTIVE AND IRREVERSIBLE: the stored bytes are replaced and the original is not kept
/// anywhere. That is accepted for avatars specifically — they are display-only, never a detection
/// or alignment input — but it is why this only ever runs when an admin presses the button, why
/// every row it touches is logged with its before/after size and dimensions, and why the dry run
/// exists. Nothing here is wired to startup.
/// <para>
/// A re-encode moves the avatar ETag, which is <c>(userId, length, contentType)</c>, so browsers
/// refetch each rewritten avatar once. That is the intended outcome: the point is that they stop
/// fetching megabytes.
/// </para>
/// </remarks>
public sealed class AvatarNormalizer
{
    /// <summary>
    /// Stored size above which an avatar is a candidate. The current pipeline lands a 512 px WebP
    /// at roughly 20-60 kB, so 256 kB is far above anything it produces and far below the
    /// multi-megabyte camera originals that predate it — no avatar written by the upload path can
    /// be caught by this, and no legacy full-resolution one can escape it.
    /// </summary>
    public const int MaxStoredBytes = 256 * 1024;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ILogger<AvatarNormalizer> logger;

    public AvatarNormalizer(IDbContextFactory<BlocwerkDbContext> dbContextFactory, ILogger<AvatarNormalizer> logger)
    {
        this.dbContextFactory = dbContextFactory;
        this.logger = logger;
    }

    /// <summary>
    /// Normalises every oversized avatar. With <paramref name="dryRun"/> set, the work is done in
    /// full — decode, scale, encode — and the result reported, but nothing is saved.
    /// </summary>
    public async Task<AvatarNormalizeSummary> NormalizeAsync(bool dryRun, MaintenanceJobLog log, CancellationToken ct)
    {
        var startedAt = TimeProvider.System.GetTimestamp();

        // Ids only, up front, so the run does not sit on a connection while it encodes; each
        // candidate then opens its own short-lived context. The size gate is a server-side
        // length(bytea), so listing candidates never moves an avatar.
        List<Guid> candidates;
        await using (var db = await dbContextFactory.CreateDbContextAsync(ct))
        {
            candidates = await db.Users
                .AsNoTracking()
                .Where(u => u.AvatarImage != null && u.AvatarImage.Length > MaxStoredBytes)
                .Select(u => u.Id)
                .ToListAsync(ct);
        }

        log.Append($"{candidates.Count} avatar(s) over {MaxStoredBytes / 1024} kB.");

        var counts = new int[3];
        long before = 0;
        long after = 0;
        var index = 0;

        foreach (var userId in candidates)
        {
            ct.ThrowIfCancellationRequested();
            log.Report($"{++index} / {candidates.Count} avatars...");

            var outcome = await NormalizeOneAsync(userId, dryRun, log, ct);
            counts[(int)outcome.Result]++;
            before += outcome.Before;
            after += outcome.After;
        }

        var summary = new AvatarNormalizeSummary(
            dryRun, candidates.Count, counts[0], counts[1], counts[2],
            before, after, TimeProvider.System.GetElapsedTime(startedAt));

        logger.LogInformation("Avatar normalisation finished: {Summary}", summary);
        return summary;
    }

    private async Task<(AvatarOutcome Result, long Before, long After)> NormalizeOneAsync(
        Guid userId, bool dryRun, MaintenanceJobLog log, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user?.AvatarImage is not { Length: > 0 } original)
        {
            return (AvatarOutcome.Skipped, 0, 0);
        }

        var wasType = user.AvatarContentType ?? "unknown";
        var wasSize = AvatarImageEncoder.Measure(original);

        byte[] encoded;
        string contentType;
        try
        {
            (encoded, contentType) = AvatarImageEncoder.Scale(original);
        }
        catch (InvalidOperationException ex)
        {
            log.Append($"FAILED user {userId}: {ex.Message}");
            logger.LogWarning(ex, "Could not normalise the avatar of {UserId}", userId);
            return (AvatarOutcome.Failed, original.Length, original.Length);
        }

        // Never make an avatar bigger. A source that is already small in pixels but large in bytes
        // could in principle re-encode larger, and rewriting it would be a pure loss.
        if (encoded.Length >= original.Length)
        {
            log.Append($"SKIP user {userId}: re-encoding would not shrink it ({original.Length} B).");
            return (AvatarOutcome.Skipped, original.Length, original.Length);
        }

        var isSize = AvatarImageEncoder.Measure(encoded);
        log.Append(
            $"{(dryRun ? "WOULD REWRITE" : "REWROTE")} user {userId}: " +
            $"{Describe(original.Length, wasSize, wasType)} -> {Describe(encoded.Length, isSize, contentType)}");
        logger.LogInformation(
            "Avatar normalisation {Mode} user {UserId}: {BeforeBytes} B {BeforeType} {BeforeDims} -> " +
            "{AfterBytes} B {AfterType} {AfterDims}",
            dryRun ? "would rewrite" : "rewrote", userId,
            original.Length, wasType, Dimensions(wasSize),
            encoded.Length, contentType, Dimensions(isSize));

        if (!dryRun)
        {
            user.AvatarImage = encoded;
            user.AvatarContentType = contentType;
            await db.SaveChangesAsync(ct);
        }

        return (AvatarOutcome.Rewritten, original.Length, encoded.Length);
    }

    private static string Describe(int bytes, (int Width, int Height)? size, string contentType) =>
        $"{bytes} B {contentType} {Dimensions(size)}";

    private static string Dimensions((int Width, int Height)? size) =>
        size is { } s ? $"{s.Width}x{s.Height}" : "?x?";

    /// <summary>Ordered to match the counter array in <see cref="NormalizeAsync"/>.</summary>
    private enum AvatarOutcome
    {
        Rewritten = 0,
        Skipped = 1,
        Failed = 2,
    }
}
