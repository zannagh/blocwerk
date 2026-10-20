// <copyright file="WallEnhancementKinds.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Enhancement;

/// <summary>The rules and names shared by the enhancement job and its review.</summary>
public static class WallEnhancementKinds
{
    /// <summary>The prefix of every journal batch the enhancement opens.</summary>
    public const string BatchPrefix = "wall-enhance:";

    /// <summary>The batch of review writes that are not an undo (confirming a suggested colour).</summary>
    public const string ReviewBatchLabel = BatchPrefix + "review";

    /// <summary>The kinds in the order the review shows them.</summary>
    public static readonly IReadOnlyList<WallEnhancementItemKind> All =
    [
        WallEnhancementItemKind.Shape,
        WallEnhancementItemKind.Stack,
        WallEnhancementItemKind.Colour,
        WallEnhancementItemKind.Duplicate,
        WallEnhancementItemKind.Link,
        WallEnhancementItemKind.NewHold,
    ];

    /// <summary>
    /// The hold columns an undo of a written kind is about: they must still be as the steps left them, and they are put
    /// back. Everything else the steps' batches touched (the 3D sizes, facet and plane a refinement re-derives) is put back
    /// only where nobody changed it since.
    /// </summary>
    /// <param name="kind">A written kind.</param>
    /// <returns>The column names.</returns>
    public static IReadOnlySet<string> OwnedColumns(WallEnhancementItemKind kind) => kind switch
    {
        WallEnhancementItemKind.Shape => new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(Hold.X), nameof(Hold.Y), nameof(Hold.Radius), nameof(Hold.ShapePoints), nameof(Hold.ShapeControlPoints),
            nameof(Hold.ShapeHoles), nameof(Hold.OutlineSource), nameof(Hold.OutlineConfidence),
        },
        WallEnhancementItemKind.Stack => new HashSet<string>(StringComparer.Ordinal) { nameof(Hold.StackedOnHoldId) },
        _ => new HashSet<string>(StringComparer.Ordinal) { nameof(Hold.Color), nameof(Hold.ProposedColor) },
    };

    /// <summary>True for a kind its step already wrote (kept or undone per hold); false for a suggestion.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>Whether it was written.</returns>
    public static bool IsWritten(WallEnhancementItemKind kind) =>
        kind is WallEnhancementItemKind.Shape or WallEnhancementItemKind.Stack or WallEnhancementItemKind.Colour;

    /// <summary>The step's own journal batch label.</summary>
    /// <param name="stepKey">The step key.</param>
    /// <returns>The label.</returns>
    public static string BatchLabel(string stepKey) => BatchPrefix + stepKey;

    /// <summary>The label of the batch that undoes one hold of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The label.</returns>
    public static string UndoLabel(WallEnhancementItemKind kind) => $"revert:{BatchPrefix}{kind.ToString().ToLowerInvariant()}";

    /// <summary>"1 hold" / "3 holds".</summary>
    /// <param name="count">How many.</param>
    /// <param name="one">The singular.</param>
    /// <param name="many">The plural.</param>
    /// <returns>The words.</returns>
    public static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    /// <summary>The kind in plain words, plural ("Hold shapes").</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The words.</returns>
    public static string Title(WallEnhancementItemKind kind) => kind switch
    {
        WallEnhancementItemKind.Shape => "Hold shapes",
        WallEnhancementItemKind.Stack => "Holds on other holds",
        WallEnhancementItemKind.Colour => "Suggested colours",
        WallEnhancementItemKind.Duplicate => "Possible duplicates",
        WallEnhancementItemKind.Link => "Same hold on two panels",
        _ => "Possible new holds",
    };
}
