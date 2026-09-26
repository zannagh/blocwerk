// <copyright file="WallGeometryFrameSource.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Entities;

/// <summary>What defined a <see cref="WallGeometryModel"/>'s frame. Stored as its integer value; never renumber.</summary>
public enum WallGeometryFrameSource
{
    /// <summary>Printed markers (the marker solve or an uploaded file).</summary>
    Markers = 0,

    /// <summary>A feature reconstruction without markers (<c>world.frameSource = "features"</c>).</summary>
    Features = 1,
}
