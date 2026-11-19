// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Helpers;

/// <summary>One entry of the boulder browser's vocabulary: a style or a hold type, its name and its icon.</summary>
/// <typeparam name="T">The style or hold type enum.</typeparam>
/// <param name="Value">The value it stands for.</param>
/// <param name="Label">The name the cards, chips and badges show.</param>
/// <param name="Icon">The icon's SVG content on a 24 x 24 stroked view box (paths, circles), drawn in the text colour.</param>
/// <param name="Description">A short line on what it means, shown as the chips' and cards' tooltip.</param>
public sealed record BrowseDefinition<T>(T Value, string Label, string Icon, string Description = "")
    where T : struct, Enum
{
    /// <summary>Gets the URL value: the label in lower case, a space as a hyphen (<c>heel-hook</c>).</summary>
    public string Slug => Label.ToLowerInvariant().Replace(' ', '-');
}
