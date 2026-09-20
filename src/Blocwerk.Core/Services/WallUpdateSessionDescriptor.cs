using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Turns a <see cref="WallUpdateSession"/> row into the <see cref="WallUpdateSessionInfo"/> the UI reads,
/// resolving its two user ids to display names. Shared by the session service and by the big-update
/// service, which needs the same view to report "there is already an open session, started by X at T".
/// </summary>
public static class WallUpdateSessionDescriptor
{
    public static async Task<WallUpdateSessionInfo> DescribeAsync(
        BlocwerkDbContext db, WallUpdateSession session, CancellationToken ct = default)
    {
        var ids = new List<Guid>();
        if (session.CreatedByUserId is { } createdBy)
        {
            ids.Add(createdBy);
        }

        if (session.LastActiveByUserId is { } lastBy && !ids.Contains(lastBy))
        {
            ids.Add(lastBy);
        }

        var names = new Dictionary<Guid, string?>();
        if (ids.Count > 0)
        {
            var rows = await db.Users
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, Name = u.CustomDisplayName ?? u.DisplayName })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                names[row.Id] = row.Name;
            }
        }

        return new WallUpdateSessionInfo(
            session.Id,
            session.WallId,
            session.StagedGeneration,
            session.Phase,
            session.NeighbourIndex,
            session.CreatedAt,
            session.CreatedByUserId,
            Lookup(names, session.CreatedByUserId),
            session.UpdatedAt,
            session.LastActiveByUserId,
            Lookup(names, session.LastActiveByUserId));
    }

    private static string? Lookup(IReadOnlyDictionary<Guid, string?> names, Guid? id)
    {
        if (id is not { } key)
        {
            return null;
        }

        return names.TryGetValue(key, out var name) ? name : null;
    }
}
