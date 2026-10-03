// <copyright file="PhotoStampRow.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>A <see cref="PhotoInfoStamp"/> as its raw query returns it.</summary>
internal sealed class PhotoStampRow
{
    /// <summary>The panel (or legacy wall) row id.</summary>
    public Guid Id { get; set; }

    /// <summary>The wall the row belongs to.</summary>
    public Guid WallId { get; set; }

    /// <summary>The row's generation.</summary>
    public int Generation { get; set; }

    /// <summary>The photo's byte length.</summary>
    public long Length { get; set; }

    /// <summary>The photo's last bytes.</summary>
    public byte[]? Tail { get; set; }
}
