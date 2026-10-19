// <copyright file="WallRefreshService.Preflight.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The checks "Start" makes before anything runs. A panel update staged while another update (a step-by-step one) is
/// open on the wall is refused by the staging, but only in the background, after the 3D capture has already been
/// started with the photos; so the conflict is found here, and the user is told before anything starts.
/// </summary>
public sealed partial class WallRefreshService
{
    /// <summary>The refusal, naming who started the open update and when, and what to do about it.</summary>
    public static string OtherUpdateOpenMessage(WallUpdateSessionInfo open) =>
        $"This wall already has an update in progress, started by {open.CreatedByName ?? "another admin"} on "
        + $"{open.CreatedAt:d MMM yyyy, HH:mm} UTC (\"Update step by step\"). Finish or discard that one first; "
        + "nothing was started, and your photos are kept here.";

    /// <summary>
    /// Refuses the start when it would stage panel photos while an update that is not this run's is open on the wall.
    /// A run without panel photos (3D only) stages nothing, so it is never refused here.
    /// </summary>
    private static async Task EnsureNoOtherUpdateOpenAsync(BlocwerkDbContext db, WallRefresh refresh, IReadOnlyList<PanelPick> picks)
    {
        if (picks.All(p => p.PhotoId is null))
        {
            return;
        }

        var open = await WallUpdateSessions.FindOpenAsync(db, refresh.WallId);
        if (open is null || open.Id == refresh.UpdateSessionId)
        {
            return;
        }

        var info = await WallUpdateSessionDescriptor.DescribeAsync(db, open);
        throw new UserFacingException(OtherUpdateOpenMessage(info));
    }
}
