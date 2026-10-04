// <copyright file="PanelPhotoVersion.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>
/// The <see cref="WallPhotoTag.Version"/> of a LIVE panel photo: its generation, plus its in-place
/// <see cref="Entities.WallPanel.PhotoRevision"/> in the high bits. An uncropped panel (revision 0) keeps the plain
/// generation it always had, so no ETag or cached variant changes for it.
/// </summary>
public static class PanelPhotoVersion
{
    /// <summary>The version token.</summary>
    /// <param name="generation">The panel's generation.</param>
    /// <param name="photoRevision">The panel's photo revision.</param>
    /// <returns>The token.</returns>
    public static long Of(int generation, int photoRevision) => ((long)photoRevision << 32) | (uint)generation;
}
