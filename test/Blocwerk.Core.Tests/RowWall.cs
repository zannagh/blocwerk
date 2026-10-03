// <copyright file="RowWall.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Tests;

/// <summary>The ids <see cref="PanelUpdateCarryFixture.SeedAsync"/> seeds: three gen-2 panels in row 0.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="PanelIds">The gen-2 panels by column (0, 1, 2).</param>
/// <param name="CentreHoldId">The hold on the centre panel.</param>
/// <param name="NeighbourHoldId">The hold on the (1,0) panel.</param>
/// <param name="FarHoldId">The named hold on the (2,0) panel.</param>
/// <param name="FarBoulderId">A boulder on the far hold only.</param>
/// <param name="SpanBoulderId">A boulder on the neighbour and far holds.</param>
internal sealed record RowWall(
    Guid WallId,
    Guid[] PanelIds,
    Guid CentreHoldId,
    Guid NeighbourHoldId,
    Guid FarHoldId,
    Guid FarBoulderId,
    Guid SpanBoulderId);
