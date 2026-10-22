// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Data;

/// <summary>
/// Which old <see cref="ActivityType.AttemptLogged"/> entry recorded which attempt, for <see cref="AttemptActivityBackfill"/>.
/// An entry was written right after its attempt was saved, by the same user on the same boulder with the attempt's type
/// as its details, so within one such group the attempts are matched in time order, each to the earliest unmatched entry
/// written at or after it. An entry is an orphan (its attempt was deleted) when its boulder is gone (the attempts went
/// with it), or when its group has no attempt left at all, or every attempt of the group found its entry. A group where
/// some attempt found none is left undecided: nothing in it is deleted.
/// </summary>
public static class AttemptActivityMatch
{
    /// <summary>The links (entry, attempt) and the orphan entries of <paramref name="entries"/>.</summary>
    public static (IReadOnlyList<(Guid EntryId, Guid AttemptId)> Links, IReadOnlySet<Guid> Orphans) Plan(
        IReadOnlyList<UnlinkedAttemptEntry> entries, IReadOnlyList<UnlinkedAttempt> attempts)
    {
        var links = new List<(Guid, Guid)>();
        var orphans = new HashSet<Guid>();
        var byGroup = attempts.ToLookup(a => (a.UserId, (Guid?)a.BoulderId, a.Type));
        foreach (var group in entries.GroupBy(e => (e.UserId, e.BoulderId, Type: TypeOf(e.Details))))
        {
            if (group.Key.BoulderId is null)
            {
                orphans.UnionWith(group.Select(e => e.Id));
                continue;
            }

            if (group.Key.Type is not { } type)
            {
                continue; // details that name no attempt type: nothing to match on
            }

            var (matched, unmatched, allFound) = MatchGroup(group.ToList(), byGroup[(group.Key.UserId, group.Key.BoulderId, type)].ToList());
            links.AddRange(matched);
            if (allFound)
            {
                orphans.UnionWith(unmatched);
            }
        }

        return (links, orphans);
    }

    private static (List<(Guid, Guid)> Matched, List<Guid> Unmatched, bool AllFound) MatchGroup(
        List<UnlinkedAttemptEntry> entries, List<UnlinkedAttempt> attempts)
    {
        var open = entries.OrderBy(e => e.Timestamp).ToList();
        var matched = new List<(Guid, Guid)>();
        var allFound = true;
        foreach (var attempt in attempts.OrderBy(a => a.Timestamp))
        {
            var entry = open.FirstOrDefault(e => e.Timestamp >= attempt.Timestamp);
            if (entry is null)
            {
                allFound = false;
                continue;
            }

            open.Remove(entry);
            matched.Add((entry.Id, attempt.Id));
        }

        return (matched, open.Select(e => e.Id).ToList(), allFound);
    }

    private static AttemptType? TypeOf(string? details) =>
        Enum.TryParse<AttemptType>(details, ignoreCase: false, out var type) && Enum.IsDefined(type) ? type : null;
}
