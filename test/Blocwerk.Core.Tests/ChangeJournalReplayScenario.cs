// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>How the touch-up between staging and promote is written in a <see cref="ChangeJournalReplayFixture"/> run.</summary>
public enum TouchupMode
{
    /// <summary>No touch-up: a defaults-only update.</summary>
    None,

    /// <summary>Raw SaveChanges outside any batch: how touch-ups were journalled before the C7 fix ("adhoc" batches).</summary>
    Adhoc,

    /// <summary>Through the real <see cref="WallPanelService"/> staged-edit methods.</summary>
    Service,

    /// <summary>
    /// Through the session-scoped staged edits the update's UI and API make, inside an open update session whose old hold
    /// is confirmed "Changed" onto the hold the touch-up deletes (so the delete releases that twin in the same save).
    /// </summary>
    Session,
}

/// <summary>The ids of one source update and the replay target it is replayed onto.</summary>
public sealed class ChangeJournalReplayScenario
{
    public Guid WallId { get; init; }

    public Guid OldHoldId { get; init; }

    public Guid BoulderId { get; init; }

    public Guid PanelId { get; set; }

    public Guid TwinId { get; } = Guid.NewGuid();

    public Guid DeletedId { get; } = Guid.NewGuid();

    /// <summary>The hold added in the touch-up (the service picks its own id).</summary>
    public Guid AddedId { get; set; } = Guid.NewGuid();

    /// <summary>The open update session of a <see cref="TouchupMode.Session"/> run.</summary>
    public Guid SessionId { get; set; }

    /// <summary>How many carry verdicts the session-scoped delete released from its twin.</summary>
    public int ReleasedTwins { get; set; }

    public IDbContextFactory<BlocwerkDbContext>? TargetFactory { get; set; }

    public ChangeJournal? TargetJournal { get; set; }
}
