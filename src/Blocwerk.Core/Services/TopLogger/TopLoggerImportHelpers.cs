using System.Globalization;
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
    /// Loads a gym's calibration once per distinct gym (cached like the gym cache, so no per-tick N+1).
    /// Returns null when the gym is unknown or has no calibrated grade points, so the caller falls back
    /// to the raw-grade formatter and <see cref="ClassifyAttempt"/>.
    /// </summary>
    public static async Task<GymCalibrationData?> LoadCalibrationAsync(
        BlocwerkDbContext db,
        Dictionary<Guid, GymCalibrationData?> cache,
        ExternalGym? gym,
        CancellationToken cancellationToken)
    {
        if (gym is null)
        {
            return null;
        }

        if (cache.TryGetValue(gym.Id, out GymCalibrationData? cached))
        {
            return cached;
        }

        List<(string Grade, int Points)> points = (await db.GymGradePoints
            .Where(p => p.ExternalGymId == gym.Id)
            .OrderBy(p => p.Points)
            .Select(p => new { p.Grade, p.Points })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Select(p => (p.Grade, p.Points))
            .ToList();

        GymCalibrationData? data = points.Count == 0 ? null : new GymCalibrationData(points, gym.FlashBonusPoints);
        cache[gym.Id] = data;
        return data;
    }

    // Small tolerance for the double points compare (source points are effectively integers).
    private const double PointsTolerance = 0.5;

    /// <summary>
    /// Derives an ascent's grade and flash/send from a gym's calibration and the ascent's points: the
    /// calibrated grade whose base points is the LARGEST value ≤ points, then flash when the delta above
    /// that base reaches the flash bonus. Returns <c>(null, false)</c> when the points fall below the
    /// lowest calibrated grade or the map is empty (uncalibratable). Pure.
    /// </summary>
    public static (string? Grade, bool IsFlash) DeriveFromCalibration(
        IReadOnlyList<(string Grade, int Points)> sortedPoints, int flashBonus, double points)
    {
        string? grade = null;
        int basePoints = 0;
        foreach ((string Grade, int Points) entry in sortedPoints)
        {
            // sortedPoints is ascending; the last entry still ≤ points is the matched (base) grade.
            if (entry.Points <= points + PointsTolerance)
            {
                grade = entry.Grade;
                basePoints = entry.Points;
            }
            else
            {
                break;
            }
        }

        if (grade is null)
        {
            return (null, false);
        }

        double delta = points - basePoints;
        bool isFlash = flashBonus > 0 && delta >= flashBonus - PointsTolerance;
        return (grade, isFlash);
    }

    /// <summary>
    /// Projects a tick to an ascent. When the gym has a calibration and the tick is a topped/ticked
    /// ascent with points, the grade and flash/send are derived deterministically from that calibration
    /// (overriding the formatter and <see cref="ClassifyAttempt"/>). Otherwise falls back to the tick's
    /// own mapping then the user's raw-grade resolution, with <see cref="ClassifyAttempt"/> for the type.
    /// Pure — the caller wires the activity.
    /// </summary>
    public static ExternalAscent BuildAscent(
        Guid userId,
        TopLoggerTick tick,
        ExternalGym? gym,
        IReadOnlyDictionary<string, string> gradeMap,
        GymCalibrationData? calibration)
    {
        string? mapped = tick.MappedFontGrade;
        if (string.IsNullOrWhiteSpace(mapped)
            && !string.IsNullOrWhiteSpace(tick.RawGrade)
            && gradeMap.TryGetValue(tick.RawGrade, out string? fromUser))
        {
            mapped = fromUser;
        }

        AttemptType type = ClassifyAttempt(tick);

        // A calibrated gym resolves grade + flash/send from the ascent's points, superseding the raw
        // grade path. Only for topped/ticked ascents with points; uncalibratable points leave the
        // fallback in place.
        bool topped = tick.Ticked || tick.Topped == true;
        if (calibration is not null && topped && tick.Points is { } points)
        {
            (string? grade, bool isFlash) =
                DeriveFromCalibration(calibration.Sorted, calibration.FlashBonus, points);
            if (grade is not null)
            {
                mapped = grade;
                type = isFlash ? AttemptType.Flash : AttemptType.Send;
            }
        }

        return new ExternalAscent
        {
            UserId = userId,
            Source = ExternalSource.TopLogger,
            ExternalId = tick.ExternalId,
            ClimbId = Truncate(tick.ClimbId, 64),
            ClimbName = Truncate(string.IsNullOrWhiteSpace(tick.ClimbName) ? "Unknown climb" : tick.ClimbName, 256)!,
            ExternalGymId = gym?.Id,
            // TopLogger returns the tick's local offset (e.g. +02:00); Blocwerk stores everything as
            // UTC (Npgsql's timestamptz rejects a non-zero offset), so normalise before persisting.
            LoggedAt = tick.LoggedAt!.Value.ToUniversalTime(),
            Type = type,
            Ticked = tick.Ticked,
            Topped = tick.Topped,
            Points = tick.Points,
            RawGrade = Truncate(tick.RawGrade, 32),
            MappedGrade = Truncate(mapped, 16),
            NeedsGradeMapping = string.IsNullOrWhiteSpace(mapped),
        };
    }

    // tickType values TopLogger uses to explicitly tag a first-try success (case-insensitive).
    // "onsight"/"flash" both mean topped first try; "redpoint" is a worked send, never a flash.
    private static readonly string[] FlashTickTypes = ["flash", "flashed", "onsight"];

    /// <summary>
    /// Classifies a tick as Attempt / Send / Flash. A flash is detected two ways, both reliable and
    /// mutually reinforcing: (1) TopLogger's explicit first-try tickType tag (flash/onsight), or (2) a
    /// score-system points bonus — a gym awards points ABOVE the climb's base grade only for a flash, so
    /// <c>points &gt; base grade</c> means it was flashed (a redpoint scores exactly the base grade). We do
    /// NOT infer flash from tickIndex/first-try, which over-counts (confirmed wrong against live data).
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
        if (flashType)
        {
            return AttemptType.Flash;
        }

        // Score-system bonus: points strictly above the climb's base grade points => flashed.
        if (tick.Points is { } points
            && double.TryParse(tick.RawGrade, NumberStyles.Any, CultureInfo.InvariantCulture, out double baseGrade)
            && points > baseGrade + 0.5)
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
