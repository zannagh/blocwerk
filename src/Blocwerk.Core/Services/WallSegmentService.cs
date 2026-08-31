using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

public interface IWallSegmentService
{
    Task<List<WallSegment>> GetSegmentsAsync(Guid wallId);

    /// <summary>
    /// Replaces the wall's whole segment set, since the segment editor always submits all
    /// of them at once. An empty list clears the segments and hands the wall back to its
    /// single-plane <see cref="Wall.Angle"/> and <see cref="Wall.BorderPoints"/>.
    /// </summary>
    Task<List<WallSegment>> ReplaceSegmentsAsync(Guid wallId, IEnumerable<WallSegmentInput> segments);

    Task DeleteSegmentAsync(Guid segmentId);
}

/// <summary>
/// One segment as submitted by the editor. <paramref name="Points"/> are absolute
/// normalized (0..1) polygon vertices.
/// </summary>
public record WallSegmentInput(
    string Name,
    int Angle,
    List<ShapePoint> Points,
    int SortOrder = 0,
    int Yaw = 0,
    WallSegmentKind Kind = WallSegmentKind.Wall);

public class WallSegmentService : IWallSegmentService
{
    private const int MinAngle = 0;
    private const int MaxAngle = 90;
    private const int MinYaw = -90;
    private const int MaxYaw = 90;

    private readonly IDbContextFactory<BlocwerkDbContext> _dbContextFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<WallSegmentService> _logger;

    public WallSegmentService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        ILogger<WallSegmentService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    public async Task<List<WallSegment>> GetSegmentsAsync(Guid wallId)
    {
        using var op = BlocwerkMetrics.TimeOperation("WallSegment.Get", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            return await db.WallSegments
                .Where(s => s.WallId == wallId)
                .OrderBy(s => s.SortOrder)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task<List<WallSegment>> ReplaceSegmentsAsync(Guid wallId, IEnumerable<WallSegmentInput> segments)
    {
        using var op = BlocwerkMetrics.TimeOperation("WallSegment.Save", wallId);
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

            var wall = await db.Walls
                .Include(w => w.Segments)
                .FirstOrDefaultAsync(w => w.Id == wallId);

            if (wall is null)
            {
                _logger.LogWarning("Wall {WallId} not found while replacing segments for {UserId}", wallId, user.Id);
                throw new InvalidOperationException("Wall not found");
            }

            db.WallSegments.RemoveRange(wall.Segments);

            var created = new List<WallSegment>();
            foreach (var input in segments)
            {
                if (input.Points.Count < 3)
                {
                    _logger.LogWarning("Rejected segment '{SegmentName}' on wall {WallId} with too few points ({PointCount})", input.Name, wallId, input.Points.Count);
                    throw new InvalidOperationException("A segment needs at least three points");
                }

                var segment = new WallSegment
                {
                    WallId = wallId,
                    Name = input.Name,
                    Angle = Math.Clamp(input.Angle, MinAngle, MaxAngle),
                    Yaw = Math.Clamp(input.Yaw, MinYaw, MaxYaw),
                    Kind = input.Kind,
                    Points = input.Points,
                    SortOrder = input.SortOrder,
                };

                created.Add(segment);
                db.WallSegments.Add(segment);
            }

            await db.SaveChangesAsync();
            _logger.LogInformation("Segments replaced on wall {WallId} ({Count}) by {UserId}", wallId, created.Count, user.Id);
            return created.OrderBy(s => s.SortOrder).ToList();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    public async Task DeleteSegmentAsync(Guid segmentId)
    {
        using var op = BlocwerkMetrics.TimeOperation("WallSegment.Delete");
        try
        {
            var user = await _currentUserService.GetCurrentUserAsync();
            await using var db = await _dbContextFactory.CreateDbContextAsync();
            db.CurrentUserId = user.Id;

            var segment = await db.WallSegments.FirstOrDefaultAsync(s => s.Id == segmentId);

            if (segment is null)
            {
                _logger.LogWarning("Segment {SegmentId} not found while deleting for {UserId}", segmentId, user.Id);
                throw new InvalidOperationException("Segment not found");
            }

            await WallAdminGuard.EnsureWallAdminAsync(db, segment.WallId, user.Id, CancellationToken.None);

            db.WallSegments.Remove(segment);
            await db.SaveChangesAsync();
            _logger.LogInformation("Segment {SegmentId} deleted from wall {WallId} by {UserId}", segmentId, segment.WallId, user.Id);
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }
}
