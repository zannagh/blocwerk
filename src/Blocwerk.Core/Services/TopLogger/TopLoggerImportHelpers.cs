using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Stateless helpers behind <see cref="TopLoggerImportService"/>: the dedupe/grade lookups, the
/// lazy gym upsert, and the pure tick-to-ascent projection. Split out to keep the service file small.
/// </summary>
internal static class TopLoggerImportHelpers
{
    /// <summary>
    /// The external ids already imported for this user, used to dedupe against the database.
    /// </summary>
    public static async Task<HashSet<string>> LoadExistingIdsAsync(
        BlocwerkDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        List<string> ids = await db.ExternalAscents
            .Where(a => a.UserId == userId && a.Source == ExternalSource.TopLogger)
            .Select(a => a.ExternalId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    /// <summary>
    /// The user's manual raw-grade → Font-grade resolutions, keyed by raw grade token.
    /// </summary>
    public static async Task<Dictionary<string, string>> LoadGradeMapAsync(
        BlocwerkDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        return await db.UserGradeMappings
            .Where(m => m.UserId == userId)
            .ToDictionaryAsync(m => m.RawGradeKey, m => m.FontGrade, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the global <see cref="ExternalGym"/> for a tick, creating it once per (source, id).
    /// The cache also covers the in-batch case so two ticks at a new gym do not double-insert.
    /// </summary>
    public static async Task<ExternalGym?> GetOrCreateGymAsync(
        BlocwerkDbContext db,
        Dictionary<string, ExternalGym> cache,
        TopLoggerTick tick,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tick.GymId))
        {
            return null;
        }

        if (cache.TryGetValue(tick.GymId, out ExternalGym? cached))
        {
            return cached;
        }

        ExternalGym? gym = await db.ExternalGyms
            .FirstOrDefaultAsync(
                g => g.Source == ExternalSource.TopLogger && g.ExternalId == tick.GymId, cancellationToken)
            .ConfigureAwait(false);

        if (gym is null)
        {
            gym = new ExternalGym
            {
                Source = ExternalSource.TopLogger,
                ExternalId = tick.GymId,
                Name = Truncate(string.IsNullOrWhiteSpace(tick.GymName) ? tick.GymId : tick.GymName, 256)!,
                Slug = Truncate(tick.GymSlug, 256),
            };
            db.ExternalGyms.Add(gym);
        }

        cache[tick.GymId] = gym;
        return gym;
    }

    /// <summary>
    /// Sets the imported activity's gym when it does not already have one (imports carry a gym in
    /// place of a wall). The activity is freshly tracked by the clustering call, so it is in Local.
    /// </summary>
    public static void AttachGymToActivity(BlocwerkDbContext db, Guid activityId, ExternalGym? gym)
    {
        if (gym is null)
        {
            return;
        }

        Activity? activity = db.Activities.Local.FirstOrDefault(a => a.Id == activityId);
        if (activity is not null && activity.ExternalGymId is null)
        {
            activity.ExternalGymId = gym.Id;
        }
    }

    /// <summary>
    /// Projects a tick to an ascent: grade via the tick's own mapping then the user's, tick type to
    /// Flash/Send, and the flag when no grade could be resolved. Pure — the caller wires the activity.
    /// </summary>
    public static ExternalAscent BuildAscent(
        Guid userId, TopLoggerTick tick, ExternalGym? gym, IReadOnlyDictionary<string, string> gradeMap)
    {
        string? mapped = tick.MappedFontGrade;
        if (string.IsNullOrWhiteSpace(mapped)
            && !string.IsNullOrWhiteSpace(tick.RawGrade)
            && gradeMap.TryGetValue(tick.RawGrade, out string? fromUser))
        {
            mapped = fromUser;
        }

        return new ExternalAscent
        {
            UserId = userId,
            Source = ExternalSource.TopLogger,
            ExternalId = tick.ExternalId,
            ClimbName = Truncate(string.IsNullOrWhiteSpace(tick.ClimbName) ? "Unknown climb" : tick.ClimbName, 256)!,
            ExternalGymId = gym?.Id,
            // TopLogger returns the tick's local offset (e.g. +02:00); Blocwerk stores everything as
            // UTC (Npgsql's timestamptz rejects a non-zero offset), so normalise before persisting.
            LoggedAt = tick.LoggedAt!.Value.ToUniversalTime(),
            Type = ClassifyAttempt(tick),
            Ticked = tick.Ticked,
            Topped = tick.Topped,
            Points = tick.Points,
            RawGrade = Truncate(tick.RawGrade, 32),
            MappedGrade = Truncate(mapped, 16),
            NeedsGradeMapping = string.IsNullOrWhiteSpace(mapped),
        };
    }

    // tickType strings we treat as a flash (case-insensitive). The POC's only climbLogs fixture is
    // hand-authored and uses the literal "flash", but live data showed zero literal-"flash" ticks
    // across 2248 rows, so the string alone is not reliable; the try-index heuristic below backs it up.
    private static readonly string[] FlashTickTypes = ["flash", "flashed"];

    /// <summary>
    /// Classifies a tick as Attempt / Send / Flash from its ticked/topped/tickType/tryIndex fields.
    /// </summary>
    private static AttemptType ClassifyAttempt(TopLoggerTick tick)
    {
        // Not successfully climbed → a logged attempt that never counted as a send/top.
        if (!tick.Ticked && tick.Topped != true)
        {
            return AttemptType.Attempt;
        }

        bool flashType = Array.Exists(
            FlashTickTypes, t => string.Equals(t, tick.TickType, StringComparison.OrdinalIgnoreCase));

        // ASSUMPTION (verify against live data): tryIndex is 1-based — the POC fixture logs a flash at
        // tryIndex 1 and a redpoint at tryIndex 4. A successful ascent topped on the first attempt is a
        // flash. tryIndex defaults to 0 when the API omits it, so we require == 1 (a proven first try)
        // rather than <= 1, which would misread an unknown-try send as a flash.
        bool firstTry = tick.TryIndex == 1;

        if (flashType || firstTry)
        {
            return AttemptType.Flash;
        }

        return AttemptType.Send;
    }

    /// <summary>
    /// Truncates to a column's max length, preserving null. Guards against an over-long name/grade
    /// from the source overrunning the mapped entity columns.
    /// </summary>
    public static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
