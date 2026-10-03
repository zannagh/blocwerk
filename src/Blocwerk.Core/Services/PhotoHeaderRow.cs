// <copyright file="PhotoHeaderRow.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>The first bytes of a stored photo (<see cref="PanelPhotoInfoLoader"/>'s raw query row).</summary>
internal sealed class PhotoHeaderRow
{
    /// <summary>The panel (or legacy wall) row id.</summary>
    public Guid Id { get; set; }

    /// <summary>The wall the row belongs to.</summary>
    public Guid WallId { get; set; }

    /// <summary>The photo's leading bytes.</summary>
    public byte[]? Header { get; set; }
}
