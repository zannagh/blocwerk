// <copyright file="BoulderMoveLines.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The lines of the boulder page's notice about holds that moved or were not found again. A hold is named by its name,
/// else by its colour and kind ("The blue crimp"); two holds that would read alike are told apart by their size when it
/// is known, and lines that still say exactly the same are folded into one ("2 blue holds"), so the notice never
/// repeats itself. Pure.
/// </summary>
internal static class BoulderMoveLines
{
    /// <summary>The notice's lines, in the order of <paramref name="moves"/>.</summary>
    /// <param name="moves">The boulder's moves, most serious first.</param>
    /// <returns>One line per distinguishable hold.</returns>
    public static IReadOnlyList<BoulderMoveLine> Build(IReadOnlyList<BoulderMoveView> moves)
    {
        var shared = moves.GroupBy(Label).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return moves
            .Select(m => (Move: m, Label: shared.Contains(Label(m)) && m.HoldSizeCm is { } cm ? $"{Label(m)} (about {cm} cm)" : Label(m)))
            .GroupBy(l => (l.Label, l.Move.Text, l.Move.Outcome, l.Move.Source))
            .Select(g => g.Count() == 1
                ? new BoulderMoveLine(g.Key.Label, g.Key.Text, g.Key.Outcome, g.Key.Source)
                : new BoulderMoveLine(Several(g.First().Move, g.Count()), g.Key.Text.Replace("check this hold", "check these holds"), g.Key.Outcome, g.Key.Source))
            .ToList();
    }

    /// <summary>Which hold a line is about: its name, else "The white crimp" or "The white hold", else "A hold".</summary>
    /// <param name="move">The move.</param>
    /// <returns>The label.</returns>
    public static string Label(BoulderMoveView move)
    {
        if (!string.IsNullOrWhiteSpace(move.HoldName))
        {
            return move.HoldName;
        }

        var kind = string.IsNullOrWhiteSpace(move.HoldKind) ? "hold" : move.HoldKind;
        return string.IsNullOrWhiteSpace(move.HoldColor)
            ? (kind == "hold" ? "A hold" : $"A {kind}")
            : $"The {move.HoldColor} {kind}";
    }

    /// <summary>"2 blue holds", "3 holds", or "2 × Yellow crimp" for holds that share a name.</summary>
    private static string Several(BoulderMoveView move, int count)
    {
        if (!string.IsNullOrWhiteSpace(move.HoldName))
        {
            return $"{count} × {move.HoldName}";
        }

        var kind = string.IsNullOrWhiteSpace(move.HoldKind) ? "hold" : move.HoldKind;
        return string.IsNullOrWhiteSpace(move.HoldColor) ? $"{count} {kind}s" : $"{count} {move.HoldColor} {kind}s";
    }
}
