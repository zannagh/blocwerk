// <copyright file="CoverageCellStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>How the capture's cameras see a cell. Stored by name; never rename.</summary>
public enum CoverageCellStatus
{
    /// <summary>Seen from 3+ directions, at least once not steeply, at 2 mm per pixel or finer.</summary>
    Good = 0,

    /// <summary>Seen from fewer than 3 directions: its depth is poorly pinned down.</summary>
    FewDirections = 1,

    /// <summary>Only seen more than 70° off face-on.</summary>
    Grazing = 2,

    /// <summary>Only seen coarser than 2 mm per pixel (far away).</summary>
    LowResolution = 3,

    /// <summary>No camera sees it.</summary>
    Never = 4,

    /// <summary>Not rated: the facet under a volume, or a corner of its region that lies inside the wall.</summary>
    Hidden = 5,
}

/// <summary>Which side of a volume a surface sample is on (in its facet's frame).</summary>
public enum VolumeFace
{
    /// <summary>Faces out of the wall.</summary>
    Front = 0,

    /// <summary>Faces down the facet (toward the floor).</summary>
    Underside = 1,

    /// <summary>Faces up the facet.</summary>
    TopSide = 2,

    /// <summary>Faces the facet's left (−a).</summary>
    LeftSide = 3,

    /// <summary>Faces the facet's right (+a).</summary>
    RightSide = 4,
}

/// <summary>Whose camera positions the recipe check used.</summary>
public enum CoveragePoseSource
{
    /// <summary>No posed camera at all.</summary>
    None = 0,

    /// <summary>The registered video frames.</summary>
    Video = 1,

    /// <summary>The capture photos (the video frames' poses are not reported).</summary>
    Photos = 2,
}

/// <summary>The one-character cell codes of <see cref="FacetCoverage.Cells"/>.</summary>
public static class CoverageCellCodes
{
    /// <summary>The code of a rating.</summary>
    /// <param name="status">The rating.</param>
    /// <returns>g, d, a, r, n or '.'.</returns>
    public static char Code(CoverageCellStatus status) => status switch
    {
        CoverageCellStatus.Good => 'g',
        CoverageCellStatus.FewDirections => 'd',
        CoverageCellStatus.Grazing => 'a',
        CoverageCellStatus.LowResolution => 'r',
        CoverageCellStatus.Never => 'n',
        _ => '.',
    };

    /// <summary>The rating of a code.</summary>
    /// <param name="code">The code.</param>
    /// <returns>The rating (<see cref="CoverageCellStatus.Hidden"/> for anything unknown).</returns>
    public static CoverageCellStatus Status(char code) => code switch
    {
        'g' => CoverageCellStatus.Good,
        'd' => CoverageCellStatus.FewDirections,
        'a' => CoverageCellStatus.Grazing,
        'r' => CoverageCellStatus.LowResolution,
        'n' => CoverageCellStatus.Never,
        _ => CoverageCellStatus.Hidden,
    };

    /// <summary>The counts of a list of ratings (hidden ones are not counted).</summary>
    /// <param name="statuses">The ratings.</param>
    /// <returns>The counts.</returns>
    public static CoverageCounts Count(IEnumerable<CoverageCellStatus> statuses)
    {
        var n = new int[5];
        foreach (var s in statuses)
        {
            if (s != CoverageCellStatus.Hidden)
            {
                n[(int)s]++;
            }
        }

        return new CoverageCounts(n.Sum(), n[0], n[1], n[2], n[3], n[4]);
    }
}
