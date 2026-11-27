// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Scan;

/// <summary>The validator's findings, capped so a broken 36 000-frame manifest answers with a readable list.</summary>
public sealed class ScanManifestErrors
{
    /// <summary>The most findings reported; one more line says how many were left out.</summary>
    public const int Cap = 40;

    private readonly List<string> items = [];
    private int dropped;

    public bool Any => items.Count > 0;

    public void Add(string error)
    {
        if (items.Count < Cap)
        {
            items.Add(error);
        }
        else
        {
            dropped++;
        }
    }

    public IReadOnlyList<string> ToList() =>
        dropped == 0 ? items.ToList() : [.. items, $"… and {dropped} more problem(s) of the same kinds."];
}
