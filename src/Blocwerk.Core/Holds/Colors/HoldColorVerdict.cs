// <copyright file="HoldColorVerdict.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>Why the colour estimate of one hold proposed a colour, or why it left the colour unset.</summary>
public enum HoldColorVerdict
{
    /// <summary>A palette colour was proposed.</summary>
    Proposed = 0,

    /// <summary>Too few pixels inside the outline to say anything.</summary>
    TooFewPixels = 1,

    /// <summary>The hold looks like what surrounds it (a wooden hold on the wooden wall, or an outline off the hold).</summary>
    LikeSurroundings = 2,

    /// <summary>The coloured pixels do not agree on one hue (a two-coloured hold, or a neighbour inside the outline).</summary>
    MixedHues = 3,

    /// <summary>The hue sits between two palette colours (pink or red, orange or yellow, blue or purple).</summary>
    BetweenColors = 4,

    /// <summary>Coloured, but too weakly to tell a faded plastic hold from wood.</summary>
    Pale = 5,

    /// <summary>Some colour under a lot of grey or white: chalk, or a hold that is partly hidden.</summary>
    MostlyChalk = 6,

    /// <summary>Dark or light with a colour cast that is not the lamps' or blue: a dark purple hold, bleached wood.</summary>
    TintedGrey = 7,

    /// <summary>Neither clearly white nor clearly black (grey, a shadowed white, a chalked black).</summary>
    UnclearGrey = 8,

    /// <summary>The panel photo could not be read.</summary>
    NoPhoto = 9,

    /// <summary>The same hold on two panels gave two different colours.</summary>
    ViewsDisagree = 10,

    /// <summary>The hold's material does not come in the colour (a wooden hold only has the wood tones).</summary>
    NotForMaterial = 11,
}
