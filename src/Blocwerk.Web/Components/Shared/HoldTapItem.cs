// <copyright file="HoldTapItem.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// One hold as hold-tap.js needs it to find the hold under a tap and draw its outline: sent to the browser once per
/// panel, so the names are short and every coordinate is a whole number of <see cref="HoldTapList.Scale"/>ths of the photo.
/// </summary>
/// <param name="X">Centre X.</param>
/// <param name="Y">Centre Y.</param>
/// <param name="Radius">Radius.</param>
/// <param name="Outline">The outline as absolute x,y pairs, or null for a hold drawn as a circle (a foot: a square).</param>
/// <param name="Holes">The outline's holes, each as absolute x,y pairs, or null for a solid hold.</param>
/// <param name="Foot">Whether the hold is a foothold.</param>
/// <param name="Color">The outline colour as #rrggbb, or null for a hold without a known colour.</param>
/// <param name="ColorName">The colour's display name, or null.</param>
/// <param name="Uses">How many boulders use the hold, or null when that is not known here.</param>
/// <param name="Overlaps">Positions in the list of the holds that overlap this one substantially without being stacked with it.</param>
/// <param name="On">Position in the list of the hold this one sits on, or null for a hold on the wall.</param>
public record HoldTapItem(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("r")] int Radius,
    [property: JsonPropertyName("p")] int[]? Outline,
    [property: JsonPropertyName("h")] int[][]? Holes,
    [property: JsonPropertyName("f")] bool Foot,
    [property: JsonPropertyName("c")] string? Color,
    [property: JsonPropertyName("n")] string? ColorName,
    [property: JsonPropertyName("u")] int? Uses,
    [property: JsonPropertyName("o")] IReadOnlyList<int> Overlaps,
    [property: JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? On = null);
