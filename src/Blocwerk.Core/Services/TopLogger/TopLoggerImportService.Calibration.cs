using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// The per-gym points→grade calibration half of <see cref="TopLoggerImportService"/>: reading the gyms
/// a user climbs at, editing a gym's shared (grade → base points) map + flash bonus, and rehydrating
/// the current user's existing ascents from a saved calibration.
/// </summary>
public sealed partial class TopLoggerImportService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TopLoggerGymRef>> GetCalibratableGymsAsync(
        Guid userId, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Distinct gyms the user has TopLogger ascents at (an ascent without a gym can't be calibrated).
        IQueryable<Guid> gymIds = db.ExternalAscents
            .Where(a => a.UserId == userId && a.Source == ExternalSource.TopLogger && a.ExternalGymId != null)
            .Select(a => a.ExternalGymId!.Value)
            .Distinct();

        return await db.ExternalGyms
            .AsNoTracking()
            .Where(g => gymIds.Contains(g.Id))
            .OrderBy(g => g.Name)
            .Select(g => new TopLoggerGymRef(g.Id, g.Name))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<GymCalibration> GetGymCalibrationAsync(Guid gymId, CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        ExternalGym? gym = await db.ExternalGyms
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == gymId, cancellationToken)
            .ConfigureAwait(false);

        List<GymGradePointDto> points = await db.GymGradePoints
            .AsNoTracking()
            .Where(p => p.ExternalGymId == gymId)
            .OrderBy(p => p.Points)
            .Select(p => new GymGradePointDto(p.Grade, p.Points))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new GymCalibration(points, gym?.FlashBonusPoints ?? 0);
    }

    /// <inheritdoc />
    public async Task<int> SaveGymCalibrationAsync(
        Guid gymId,
        IReadOnlyList<GymGradePointDto> points,
        int flashBonusPoints,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using BlocwerkDbContext db = await dbContextFactory.CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        db.CurrentUserId = userId;

        ExternalGym? gym = await db.ExternalGyms
            .FirstOrDefaultAsync(g => g.Id == gymId, cancellationToken)
            .ConfigureAwait(false);
        if (gym is null)
        {
            return 0;
        }

        // Keep only known Font grades with positive points; a blank/0 row is treated as "unset" and so
        // drops out of the gym's set. Last write wins on a duplicate grade.
        Dictionary<string, int> valid = new(StringComparer.Ordinal);
        foreach (GymGradePointDto point in points)
        {
            string? font = NormalizeFontGrade(point.Grade);
            if (font is null || point.Points <= 0)
            {
                continue;
            }

            valid[font] = point.Points;
        }

        gym.FlashBonusPoints = Math.Max(0, flashBonusPoints);

        // Replace the gym's grade-point set with the provided rows: update matches, add new, drop the
        // rest (an entry the user cleared to 0/blank).
        List<GymGradePoint> existing = await db.GymGradePoints
            .Where(p => p.ExternalGymId == gymId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<string, GymGradePoint> existingByGrade = existing.ToDictionary(p => p.Grade, StringComparer.Ordinal);

        foreach (GymGradePoint row in existing)
        {
            if (!valid.ContainsKey(row.Grade))
            {
                db.GymGradePoints.Remove(row);
            }
        }

        foreach ((string grade, int gradePoints) in valid)
        {
            if (existingByGrade.TryGetValue(grade, out GymGradePoint? row))
            {
                row.Points = gradePoints;
            }
            else
            {
                db.GymGradePoints.Add(new GymGradePoint { ExternalGymId = gymId, Grade = grade, Points = gradePoints });
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await RehydrateAscentsAsync(db, gymId, userId, valid, gym.FlashBonusPoints, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Re-derives the current user's topped/ticked TopLogger ascents at a gym from its saved calibration:
    /// loads them once, sets grade + flash/send for every calibratable one, and persists in a single
    /// save. Non-topped attempts and uncalibratable points are left untouched. Returns the updated count.
    /// </summary>
    private static async Task<int> RehydrateAscentsAsync(
        BlocwerkDbContext db,
        Guid gymId,
        Guid userId,
        IReadOnlyDictionary<string, int> pointsMap,
        int flashBonus,
        CancellationToken cancellationToken)
    {
        if (pointsMap.Count == 0)
        {
            return 0;
        }

        List<(string Grade, int Points)> sorted = pointsMap
            .Select(kv => (kv.Key, kv.Value))
            .OrderBy(t => t.Value)
            .ToList();

        List<ExternalAscent> ascents = await db.ExternalAscents
            .Where(a => a.UserId == userId
                && a.Source == ExternalSource.TopLogger
                && a.ExternalGymId == gymId
                && a.Points != null
                && (a.Ticked || a.Topped == true))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int updated = 0;
        foreach (ExternalAscent ascent in ascents)
        {
            (string? grade, bool isFlash) =
                TopLoggerImportHelpers.DeriveFromCalibration(sorted, flashBonus, ascent.Points!.Value);
            if (grade is null)
            {
                continue;
            }

            ascent.MappedGrade = grade;
            ascent.Type = isFlash ? AttemptType.Flash : AttemptType.Send;
            ascent.NeedsGradeMapping = false;
            updated++;
        }

        if (updated > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }
}
