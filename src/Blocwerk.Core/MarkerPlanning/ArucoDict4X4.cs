// <copyright file="ArucoDict4X4.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The OpenCV <c>DICT_4X4_50</c> ArUco dictionary as a constant table, so printable markers can be
/// drawn as vectors without any native dependency. A marker is 6 × 6 modules: a black 1-module border
/// around a 4 × 4 payload. Each entry holds the payload row-major from the top-left, most significant
/// bit first, 1 = WHITE. Checked against <c>Dictionary.generateImageMarker</c> for all 50 ids by
/// <c>ArucoDictionaryTableTests</c> (HoldDetection tests).
/// </summary>
public static class ArucoDict4X4
{
    /// <summary>The dictionary name used in plans.</summary>
    public const string DictionaryName = "DICT_4X4_50";

    /// <summary>Number of ids (0 … 49).</summary>
    public const int Count = 50;

    /// <summary>Modules per side including the black border.</summary>
    public const int ModulesPerSide = 6;

    private static readonly ushort[] Payloads =
    [
        0xB532, 0x0F9A, 0x332D, 0x9946, 0x549E, 0x79CD, 0x9E2E, 0xC4F2, 0xFEDA, 0xCF56,
        0xF991, 0x11A7, 0x0EB7, 0x2A0F, 0x24B1, 0x263E, 0x4665, 0x6600, 0x6C5E, 0x76AF,
        0x868B, 0xB02B, 0xCCD5, 0xDD82, 0xFE47, 0x9471, 0xACE4, 0xA554, 0x2123, 0x346F,
        0x4415, 0x57B2, 0x9ECF, 0xF0CB, 0x08AE, 0x0929, 0x1875, 0x04FF, 0x0DF6, 0x1C5A,
        0x1718, 0x2A28, 0x328C, 0x38B2, 0x24E8, 0x2EEB, 0x2D3F, 0x4B64, 0x502E, 0x5013,
    ];

    /// <summary>
    /// True when module (<paramref name="row"/>, <paramref name="col"/>) of marker
    /// <paramref name="id"/> is white. Rows/columns 0 and 5 are the black border; 0,0 is top-left.
    /// </summary>
    public static bool IsWhite(int id, int row, int col)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(id);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, Count);
        if (row is <= 0 or >= ModulesPerSide - 1 || col is <= 0 or >= ModulesPerSide - 1)
        {
            return false;
        }

        var bit = 15 - (((row - 1) * 4) + (col - 1));
        return ((Payloads[id] >> bit) & 1) == 1;
    }
}
