// <copyright file="RevisePickSet.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// The picks <see cref="RevisePrefillMapper"/> collects, one per current hold, in the order they were made.
/// The first mark to reach a hold decides its type, usage and source; a later one only adds itself as a source.
/// </summary>
internal sealed class RevisePickSet
{
    private readonly List<Guid> order = [];
    private readonly Dictionary<Guid, (ReviseMark Mark, RevisePickSource Source, List<Guid> Sources)> picks = [];

    /// <summary>Whether <paramref name="holdId"/> is already picked.</summary>
    public bool Contains(Guid holdId) => picks.ContainsKey(holdId);

    /// <summary>Picks <paramref name="holdId"/> for <paramref name="mark"/>, or records a convergence onto it.</summary>
    public void Add(Guid holdId, ReviseMark mark, RevisePickSource source)
    {
        if (picks.TryGetValue(holdId, out var existing))
        {
            if (!existing.Sources.Contains(mark.HoldId))
            {
                existing.Sources.Add(mark.HoldId);
            }

            return;
        }

        order.Add(holdId);
        picks[holdId] = (mark, source, [mark.HoldId]);
    }

    /// <summary>The picks, in the order they were made.</summary>
    public List<RevisePick> Build() => order
        .Select(id =>
        {
            var (mark, source, sources) = picks[id];
            return new RevisePick(id, mark.Type, mark.Usage, source) { SourceHoldIds = sources };
        })
        .ToList();
}
