// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// <c>view3d.changed</c> when the wall's ACTIVE geometry model changes: one is activated, edited while active, retired or
/// deleted. Work on inactive models (imports awaiting activation, retired leftovers) is invisible to the 3D view.
/// </summary>
internal sealed class View3DFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(WallGeometryModel)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var model = (WallGeometryModel)entry.Entity;
        var wasActive = entry.State != EntityState.Added && entry.OriginalValues.GetValue<bool>(nameof(WallGeometryModel.IsActive));
        var isActive = entry.State != EntityState.Deleted && model.IsActive;
        if (!wasActive && !isActive)
        {
            return;
        }

        if (context.Rows.Any(r => r.Kind == FeedKinds.View3DChanged && r.WallId == model.WallId))
        {
            return;
        }

        context.Add(new WallChange
        {
            WallId = model.WallId,
            Kind = FeedKinds.View3DChanged,
            EntityId = model.Id,
            Audience = FeedAudience.Wall,
        });
    }
}
