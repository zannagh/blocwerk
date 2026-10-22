// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>
/// How old attempt entries are matched to their attempts (<see cref="AttemptActivityMatch"/>), on the shapes production
/// has (sized 2026-10-22: 144 entries, 140 attempts): groups with as many entries as attempts, one with no attempt left,
/// and two with one entry more than attempts, where the entry is 60 to 200 ms after its attempt and the orphan either
/// came before every attempt or after the matched one.
/// </summary>
public class AttemptActivityMatchTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Boulder = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 8, 4, 18, 7, 50, TimeSpan.Zero);

    [Fact]
    public void ProductionShapes_LinkEveryAttempt_AndFindTheFourOrphans()
    {
        var other = Guid.NewGuid();
        UnlinkedAttemptEntry[] entries =
        [
            Entry("Send", 1.395), Entry("Send", 9.532), // a send and an orphan 8 s after it
            Entry("Flash", 1.847), Entry("Flash", 3.496), Entry("Flash", 6_652_878.264), // an orphan before both flashes
            Entry("Attempt", 10, boulder: other), Entry("Attempt", 628, boulder: other), // no attempt left in their group
        ];
        UnlinkedAttempt[] attempts = [Attempt(AttemptType.Send, 1.335), Attempt(AttemptType.Flash, 3.450), Attempt(AttemptType.Flash, 6_652_878.084)];

        var (links, orphans) = AttemptActivityMatch.Plan(entries, attempts);

        Assert.Equal(
            [(entries[0].Id, attempts[0].Id), (entries[3].Id, attempts[1].Id), (entries[4].Id, attempts[2].Id)],
            links.OrderBy(l => entries.ToList().FindIndex(e => e.Id == l.EntryId)));
        Assert.Equal(new[] { entries[1].Id, entries[2].Id, entries[5].Id, entries[6].Id }.ToHashSet(), orphans);
    }

    [Fact]
    public void AnOfflineReplay_WrittenLongAfterItsTap_StillLinks()
    {
        UnlinkedAttemptEntry[] entries = [Entry("Send", 3 * 3600), Entry("Send", 3 * 3600 + 1)];
        UnlinkedAttempt[] attempts = [Attempt(AttemptType.Send, 0), Attempt(AttemptType.Send, 600)];

        var (links, orphans) = AttemptActivityMatch.Plan(entries, attempts);

        Assert.Equal(2, links.Count);
        Assert.Empty(orphans);
    }

    [Fact]
    public void AGroupWhereAnAttemptFindsNoEntry_IsUndecided_NothingIsDeleted()
    {
        // The attempt after every entry matches none, so which entry lost its attempt cannot be told.
        UnlinkedAttemptEntry[] entries = [Entry("Send", 1), Entry("Send", 2)];
        UnlinkedAttempt[] attempts = [Attempt(AttemptType.Send, 0.5), Attempt(AttemptType.Send, 5)];

        var (links, orphans) = AttemptActivityMatch.Plan(entries, attempts);

        Assert.Single(links);
        Assert.Empty(orphans);
    }

    [Fact]
    public void AnEntryWhoseBoulderIsGone_IsAnOrphan_AndOneNamingNoTypeIsLeftAlone()
    {
        UnlinkedAttemptEntry[] entries = [Entry("Send", 1) with { BoulderId = null }, Entry(null, 1), Entry("Repeat", 1)];

        var (links, orphans) = AttemptActivityMatch.Plan(entries, []);

        Assert.Empty(links);
        Assert.Equal([entries[0].Id], orphans);
    }

    private static UnlinkedAttemptEntry Entry(string? details, double seconds, Guid? boulder = null) =>
        new(Guid.NewGuid(), User, boulder ?? Boulder, details, T0.AddSeconds(seconds));

    private static UnlinkedAttempt Attempt(AttemptType type, double seconds) =>
        new(Guid.NewGuid(), User, Boulder, type, T0.AddSeconds(seconds));
}
