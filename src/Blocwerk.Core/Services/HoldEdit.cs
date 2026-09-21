// <copyright file="HoldEdit.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The intended new state of one hold, as <c>IWallService.UpdateHoldAsync</c> takes it. Geometry is
/// always written; every appearance field is a <see cref="FieldUpdate{T}"/>, so "leave it alone" and
/// "clear it" are distinguishable for ALL of colour, material, hand-type, category, kickboard and
/// name — they were not, when this was a list of nullable parameters.
/// <para>
/// <see cref="FromEditorState"/> builds the payload a caller that already holds the complete hold sends;
/// a partial payload (a property stamp, say) simply omits the fields it does not carry.
/// </para>
/// </summary>
public sealed record HoldEdit
{
    /// <summary>Normalized X of the hold's centre. Always written.</summary>
    public required double X { get; init; }

    /// <summary>Normalized Y of the hold's centre. Always written.</summary>
    public required double Y { get; init; }

    /// <summary>Normalized radius. Always written.</summary>
    public required double Radius { get; init; }

    /// <summary>The hold's colour, or a <c>Set(null)</c> to clear it.</summary>
    public FieldUpdate<string?> Color { get; init; }

    /// <summary>The hold's material, or a <c>Set(null)</c> to clear it.</summary>
    public FieldUpdate<HoldMaterial?> Material { get; init; }

    /// <summary>The hold's hand type, or a <c>Set(null)</c> to clear it.</summary>
    public FieldUpdate<HoldHandType?> HandType { get; init; }

    /// <summary>The hold's category. Non-nullable on the entity, so there is nothing to clear to.</summary>
    public FieldUpdate<HoldCategory> Category { get; init; }

    /// <summary>Whether the hold sits on the kickboard.</summary>
    public FieldUpdate<bool> IsOnKickboard { get; init; }

    /// <summary>The traced outline, or a <c>Set(null)</c> to drop it back to the plain circle.</summary>
    public FieldUpdate<List<ShapePoint>?> ShapePoints { get; init; }

    /// <summary>The hold's name, or a <c>Set(null)</c> to clear it.</summary>
    public FieldUpdate<string?> Name { get; init; }

    /// <summary>
    /// Whether a move retires the boulders that use this hold. Big-wall panel edits pass false,
    /// because parallax between two panel photos moves a hold without anything having changed.
    /// </summary>
    public bool FlagBouldersOnMove { get; init; } = true;

    /// <summary>
    /// The payload the wall editors' batch savers send. They hold the hold's whole state, so colour,
    /// material, hand-type, category and kickboard are written outright — a null among them clears,
    /// which is what "the editor shows no colour" must mean.
    /// <para>
    /// Shape and name stay conditional on purpose: the editors send null for them to mean "I have
    /// nothing to say about this one", never "erase it". That was the old parameter list's reading of
    /// them too, and it is the behaviour those savers still rely on.
    /// </para>
    /// </summary>
    public static HoldEdit FromEditorState(
        double x,
        double y,
        double radius,
        string? color,
        HoldCategory category,
        bool isOnKickboard,
        List<ShapePoint>? shapePoints,
        string? name,
        HoldMaterial? material,
        HoldHandType? handType,
        bool flagBouldersOnMove = true) => new()
        {
            X = x,
            Y = y,
            Radius = radius,
            Color = FieldUpdate<string?>.Set(color),
            Category = FieldUpdate<HoldCategory>.Set(category),
            IsOnKickboard = FieldUpdate<bool>.Set(isOnKickboard),
            ShapePoints = shapePoints is null ? FieldUpdate<List<ShapePoint>?>.Keep : FieldUpdate<List<ShapePoint>?>.Set(shapePoints),
            Name = name is null ? FieldUpdate<string?>.Keep : FieldUpdate<string?>.Set(name),
            Material = FieldUpdate<HoldMaterial?>.Set(material),
            HandType = FieldUpdate<HoldHandType?>.Set(handType),
            FlagBouldersOnMove = flagBouldersOnMove,
        };
}
