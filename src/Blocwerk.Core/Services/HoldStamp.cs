// <copyright file="HoldStamp.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds;

namespace Blocwerk.Core.Services;

/// <summary>
/// The pipette's buffer: the properties picked off one hold, plus which of them the user ticked. Every
/// subsequent tap stamps exactly the ticked ones onto the tapped hold and leaves the rest as they were
/// — which is only expressible because <see cref="HoldEdit"/> distinguishes "leave alone" from "clear".
/// <para>
/// <b>Colour vs. material.</b> The palette is split by material: a wooden hold may only wear the three
/// brown tones, plastic only the eleven plastic ones (<see cref="HoldPalette.ColorsFor"/>). So a ticked
/// colour that the TARGET's material cannot wear has no honest reading as "colour only": clearing it
/// would destroy a property the user never pointed at, and writing it would leave an invalid pair.
/// The rule here is that <b>colour carries its material whenever, and only whenever, the two conflict</b>
/// — stamping blue onto a wooden hold makes it a blue plastic hold of the sampled material. Between
/// two holds of compatible materials (the ordinary case) an unticked material stays untouched.
/// </para>
/// </summary>
public sealed record HoldStamp
{
    /// <summary>The ticked properties — the only ones ever written.</summary>
    public required HoldStampProperty Properties { get; init; }

    /// <summary>The sampled radius.</summary>
    public double Radius { get; init; }

    /// <summary>The sampled colour key.</summary>
    public string? Color { get; init; }

    /// <summary>The sampled material.</summary>
    public HoldMaterial? Material { get; init; }

    /// <summary>The sampled hand sub-type.</summary>
    public HoldHandType? HandType { get; init; }

    /// <summary>The sampled category.</summary>
    public HoldCategory Category { get; init; }

    /// <summary>The sampled kickboard flag.</summary>
    public bool IsOnKickboard { get; init; }

    /// <summary>Reads <paramref name="source"/>'s whole appearance; <paramref name="properties"/> decides what is ever written from it.</summary>
    public static HoldStamp From(Hold source, HoldStampProperty properties) => new()
    {
        Properties = properties,
        Radius = source.Radius,
        Color = source.Color,
        Material = source.Material,
        HandType = source.HandType,
        Category = source.Category,
        IsOnKickboard = source.IsOnKickboard,
    };

    /// <summary>Whether <paramref name="property"/> is ticked.</summary>
    public bool Has(HoldStampProperty property) => (Properties & property) == property && property != HoldStampProperty.None;

    /// <summary>Nothing ticked: stamping would write nothing, so the tap is refused rather than faked.</summary>
    public bool IsEmpty => Properties == HoldStampProperty.None;

    /// <summary>
    /// True when a ticked colour drags the sampled material along, because <paramref name="target"/>'s
    /// own material cannot wear that colour. See the type remarks for why this beats the alternatives.
    /// </summary>
    public bool CarriesMaterialTo(Hold target) =>
        Has(HoldStampProperty.Color)
        && !Has(HoldStampProperty.Material)
        && !HoldPalette.IsValidFor(Color, target.Material);

    /// <summary>
    /// The partial update this stamp means for <paramref name="target"/>. Untouched properties are
    /// <see cref="FieldUpdate{T}.Keep"/> — NOT a null, which would clear them.
    /// </summary>
    public HoldEdit ToEdit(Hold target)
    {
        var writesMaterial = Has(HoldStampProperty.Material) || CarriesMaterialTo(target);

        return new HoldEdit
        {
            // A stamp is never a move: the target keeps its own position, so no boulder is retired by it.
            X = target.X,
            Y = target.Y,
            Radius = Has(HoldStampProperty.Size) ? Radius : target.Radius,
            Color = Has(HoldStampProperty.Color) ? FieldUpdate<string?>.Set(Color) : FieldUpdate<string?>.Keep,
            Material = writesMaterial ? FieldUpdate<HoldMaterial?>.Set(Material) : FieldUpdate<HoldMaterial?>.Keep,
            HandType = Has(HoldStampProperty.HandType) ? FieldUpdate<HoldHandType?>.Set(HandType) : FieldUpdate<HoldHandType?>.Keep,
            Category = Has(HoldStampProperty.Category) ? FieldUpdate<HoldCategory>.Set(Category) : FieldUpdate<HoldCategory>.Keep,
            IsOnKickboard = Has(HoldStampProperty.Kickboard) ? FieldUpdate<bool>.Set(IsOnKickboard) : FieldUpdate<bool>.Keep,
        };
    }

    /// <summary>
    /// Applies the stamp to an in-memory working copy, through the very same <see cref="ToEdit"/>
    /// payload the save will send — so what the editor draws and what the database gets cannot drift.
    /// Returns false when nothing is ticked.
    /// </summary>
    public bool ApplyTo(Hold target)
    {
        if (IsEmpty)
        {
            return false;
        }

        var edit = ToEdit(target);
        target.Radius = edit.Radius;
        target.Color = edit.Color.Or(target.Color);
        target.Material = edit.Material.Or(target.Material);
        target.HandType = edit.HandType.Or(target.HandType);
        target.Category = edit.Category.Or(target.Category);
        target.IsOnKickboard = edit.IsOnKickboard.Or(target.IsOnKickboard);
        return true;
    }
}
