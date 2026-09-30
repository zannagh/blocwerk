// <copyright file="PanelPositionName.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Refresh;

/// <summary>
/// A panel's name for people, from its grid position relative to the centre panel (panels have no names of their
/// own). Columns grow to the right, rows grow downwards, like the big-update uploader's Left/Right/Up/Down slots.
/// </summary>
public static class PanelPositionName
{
    /// <summary>"Centre panel", "Right panel", "Panel above", "Panel 2 to the left", "Panel above left", …</summary>
    public static string Describe(int col, int row)
    {
        var horizontal = col > 0 ? "right" : "left";
        var vertical = row < 0 ? "above" : "below";
        var across = Math.Abs(col);
        var down = Math.Abs(row);

        if (across == 0 && down == 0)
        {
            return "Centre panel";
        }

        if (down == 0)
        {
            return across == 1 ? $"{Capitalise(horizontal)} panel" : $"Panel {across} to the {horizontal}";
        }

        if (across == 0)
        {
            return down == 1 ? $"Panel {vertical}" : $"Panel {down} {vertical}";
        }

        return across == 1 && down == 1
            ? $"Panel {vertical} {horizontal}"
            : $"Panel {down} {vertical}, {across} {horizontal}";
    }

    /// <summary>The same name inside a sentence: "the right panel", "the panel above".</summary>
    public static string InSentence(int col, int row)
    {
        var name = Describe(col, row);
        return $"the {char.ToLowerInvariant(name[0])}{name[1..]}";
    }

    private static string Capitalise(string word) => $"{char.ToUpperInvariant(word[0])}{word[1..]}";
}
