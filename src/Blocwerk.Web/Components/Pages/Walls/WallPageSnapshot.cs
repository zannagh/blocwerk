// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// What <see cref="WallDetail"/> showed when it was left: the loaded read model and the boulder list's filter and sort,
/// so the list comes back with the same rows (the scroll position the browser restores only means something on those).
/// Held in <see cref="Blocwerk.Web.State.WallPageMemory"/>. The lists are the left page's own, never touched again by it.
/// </summary>
internal sealed record WallPageSnapshot
{
    public required Wall Wall { get; init; }

    public required bool CanCreateAnonymously { get; init; }

    public required List<Hold> Holds { get; init; }

    public required List<ShapePoint> BorderPoints { get; init; }

    public required List<WallPanelInfo> LivePanels { get; init; }

    public required List<BoulderListItem> Items { get; init; }

    public required List<Boulder> Boulders { get; init; }

    public required Dictionary<Guid, int> ReconciledHoldCounts { get; init; }

    public required Dictionary<Guid, int>? UsageCounts { get; init; }

    public required IReadOnlyList<HoldLinkPair> HoldLinks { get; init; }

    public required int HoldLinkCount { get; init; }

    public required bool IsAdmin { get; init; }

    public required bool IsModerator { get; init; }

    public required bool IsMember { get; init; }

    public required bool IsAnonymous { get; init; }

    public required Guid CurrentUserId { get; init; }

    public required WallUpdateSessionInfo? OpenUpdate { get; init; }

    public required string? ShareLink { get; init; }

    public required List<WallMember>? Members { get; init; }

    public required bool KioskConsented { get; init; }

    public required List<ActivityLogEntry>? ActivityItems { get; init; }

    public required int ActivityTotal { get; init; }

    public required int ActivityPage { get; init; }

    public required WallListFilter Filter { get; init; }
}
