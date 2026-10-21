// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>
/// The structural parts of a wall, which reload the wall (design §5.5): <c>segment.changed</c>, <c>panel.changed</c>,
/// <c>link.changed</c> and <c>volume.changed</c>, for an added, changed or deleted row alike. Only a panel is
/// generation-scoped, so only a panel's row carries <c>{ generation }</c>. A segment applies to every generation, a link
/// joins holds of two panels whose generations differ, and a volume belongs to a geometry model, so their payload is null.
/// A panel save that only touches its staged photo writes <c>panel.staged</c> for the editors instead: no viewer sees the
/// staged photo, but the update procedure's matched-session cache reads it and must hear about the change.
/// </summary>
internal sealed class WallPartFeedMapper : IFeedMapper
{
    private static readonly IReadOnlySet<string> UnseenPanel = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(WallPanel.StagedPhoto), nameof(WallPanel.StagedPhotoContentType), nameof(WallPanel.StagedPhotoSha256),
        nameof(WallPanel.StagedAt), nameof(WallPanel.StagedByUserId),
    };

    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(WallSegment), typeof(WallPanel), typeof(HoldLink), typeof(WallVolume)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        switch (entry.Entity)
        {
            case WallSegment segment:
                context.Add(StructureRows.Wall(FeedKinds.SegmentChanged, segment.WallId, segment.Id, null));
                break;
            case WallPanel panel:
                var generation = new GenerationPayload(FeedEntryValues.Value<int>(entry, nameof(WallPanel.Generation)));
                context.Add(FeedEntryValues.OnlyModified(entry, UnseenPanel)
                    ? StructureRows.Row(FeedKinds.PanelStaged, panel.WallId, panel.Id, FeedAudience.Setters, generation)
                    : StructureRows.Wall(FeedKinds.PanelChanged, panel.WallId, panel.Id, generation));
                break;
            case HoldLink link:
                context.Add(StructureRows.Wall(FeedKinds.LinkChanged, link.WallId, link.Id, null));
                break;
            case WallVolume volume:
                context.Add(StructureRows.Wall(FeedKinds.VolumeChanged, volume.WallId, volume.Id, null));
                break;
        }
    }
}
