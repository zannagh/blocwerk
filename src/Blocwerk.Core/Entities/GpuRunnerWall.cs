// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>A wall a <see cref="GpuRunner"/> serves as the wall's OWN runner (first in line for its jobs).</summary>
public class GpuRunnerWall
{
    public Guid RunnerId { get; set; }

    [ForeignKey(nameof(RunnerId))]
    public GpuRunner Runner { get; set; } = null!;

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;
}
