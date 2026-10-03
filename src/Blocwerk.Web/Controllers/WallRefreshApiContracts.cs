// <copyright file="WallRefreshApiContracts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>POST …/refresh/{refreshId}/start</c>.</summary>
/// <param name="Choices">
/// Overrides of the sorter's proposal, one per panel (<c>photoId: null</c> keeps that panel's current photo); omitted
/// or empty accepts the proposal as it is, like pressing Start on the sort screen without changing anything.
/// </param>
public sealed record RefreshStartRequest(IReadOnlyList<PanelChoice>? Choices = null);

/// <summary>Body of <c>POST …/refresh/{refreshId}/apply</c>.</summary>
/// <param name="DecisionsVersion">
/// The <see cref="RefreshSummary.DecisionsVersion"/> of the summary the caller checked (from <c>GET …/summary</c>).
/// Required: Apply promotes only what that summary describes.
/// </param>
public sealed record RefreshApplyRequest(string? DecisionsVersion);

/// <summary>What <c>GET …/refresh/{refreshId}/summary</c> answers: the confirm screen, without applying anything.</summary>
/// <param name="RefreshId">The run.</param>
/// <param name="Status">Where the run is.</param>
/// <param name="CanApply">True when Apply would be taken now: the summary is ready and no 3D check is pending.</param>
/// <param name="DecisionsVersion">The version to send to Apply; null until the summary exists.</param>
/// <param name="Summary">The confirm screen's counts, null until the panel update is prepared.</param>
/// <param name="Picks">The photo proposed (or chosen) per panel.</param>
/// <param name="Steps">The progress timeline.</param>
/// <param name="Error">Why the run stopped, or why the summary was made again.</param>
/// <param name="Check3DPending">True while the update is checked against this visit's new 3D model.</param>
public sealed record RefreshSummaryResponse(
    Guid RefreshId,
    WallRefreshStatus Status,
    bool CanApply,
    string? DecisionsVersion,
    RefreshSummary? Summary,
    IReadOnlyList<PanelPick> Picks,
    IReadOnlyList<RefreshStep> Steps,
    string? Error,
    bool Check3DPending)
{
    /// <summary>The summary of <paramref name="view"/>.</summary>
    public static RefreshSummaryResponse From(WallRefreshView view) => new(
        view.Id,
        view.Status,
        view.Status == WallRefreshStatus.ReadyToApply && !view.Check3DPending,
        view.Summary?.DecisionsVersion,
        view.Summary,
        view.Picks,
        view.Steps,
        view.Error,
        view.Check3DPending);
}

/// <summary>202 of Apply: queued with this version; poll <c>GET …/refresh/{refreshId}</c> until Done (or back at ReadyToApply).</summary>
public sealed record RefreshApplyAccepted(Guid RefreshId, string DecisionsVersion);

/// <summary>409 of Apply: refused (stale version, nothing to apply, a 3D check running); the run's current version.</summary>
public sealed record RefreshApplyRefused(string Error, WallRefreshStatus? Status, string? CurrentDecisionsVersion);
