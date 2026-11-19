// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Where in the boulder browser a URL is, the same on the Server and in the offline client. Every level has a URL of its
/// own, so reload and back return to it: nothing for the landing cards, <c>?browse=holds</c> and <c>?browse=styles</c> for
/// the two card pages, and <c>?style=all</c>, <c>?style=power</c> or <c>?hold=crimp</c> for a list.
/// </summary>
/// <param name="Level">The screen.</param>
/// <param name="Style">The style a list is limited to.</param>
/// <param name="HoldType">The hold type a list is limited to.</param>
public sealed record BoulderBrowse(BoulderBrowseLevel Level, BoulderStyle? Style = null, HoldHandType? HoldType = null)
{
    /// <summary>The query keys the browser owns.</summary>
    public const string BrowseKey = "browse", StyleKey = "style", HoldKey = "hold";

    /// <summary>Gets the landing cards.</summary>
    public static BoulderBrowse Landing { get; } = new(BoulderBrowseLevel.Landing);

    /// <summary>Gets the whole list (the "All" card).</summary>
    public static BoulderBrowse All { get; } = new(BoulderBrowseLevel.List);

    /// <summary>Gets a value indicating whether a list shows every boulder.</summary>
    public bool IsAll => Level == BoulderBrowseLevel.List && Style is null && HoldType is null;

    /// <summary>Gets the screen one step up: a list's card page (the landing for "All"), the landing for a card page.</summary>
    public BoulderBrowse Up => (Level, Style, HoldType) switch
    {
        (BoulderBrowseLevel.List, not null, _) => new(BoulderBrowseLevel.Styles),
        (BoulderBrowseLevel.List, _, not null) => new(BoulderBrowseLevel.HoldTypes),
        _ => Landing,
    };

    /// <summary>Gets the name of the screen one step up, for its back link.</summary>
    public string UpLabel => Up.Level switch
    {
        BoulderBrowseLevel.Styles => "Styles",
        BoulderBrowseLevel.HoldTypes => "Hold types",
        _ => "Boulders",
    };

    /// <summary>Gets the query a boulder's link carries, so its back link returns to this list; null off a list.</summary>
    public string? ListQuery => Level != BoulderBrowseLevel.List ? null
        : HoldType is { } hold ? $"{HoldKey}={BoulderHoldTypes.Slug(hold)}"
        : $"{StyleKey}={(Style is { } style ? BoulderStyles.Slug(style) : "all")}";

    /// <summary>Reads the browser's query values; an unknown value falls back to the landing.</summary>
    /// <param name="browse">The <c>browse</c> value.</param>
    /// <param name="style">The <c>style</c> value.</param>
    /// <param name="hold">The <c>hold</c> value.</param>
    /// <returns>The level.</returns>
    public static BoulderBrowse From(string? browse, string? style, string? hold) =>
        BoulderHoldTypes.FromSlug(hold) is { } type ? new(BoulderBrowseLevel.List, HoldType: type)
        : BoulderStyles.FromSlug(style) is { } s ? new(BoulderBrowseLevel.List, s)
        : BoulderStyles.QueryValue(style) == "all" ? All
        : browse?.Trim().ToLowerInvariant() switch
        {
            "holds" => new(BoulderBrowseLevel.HoldTypes),
            "styles" => new(BoulderBrowseLevel.Styles),
            _ => Landing,
        };

    /// <summary>The browser's query values for this level, every key named so a link replaces the previous level's.</summary>
    /// <returns>The values; null removes a key.</returns>
    public Dictionary<string, object?> Query() => new()
    {
        [BrowseKey] = Level switch
        {
            BoulderBrowseLevel.HoldTypes => "holds",
            BoulderBrowseLevel.Styles => "styles",
            _ => null,
        },
        [StyleKey] = Level == BoulderBrowseLevel.List && HoldType is null ? Style is { } s ? BoulderStyles.Slug(s) : "all" : null,
        [HoldKey] = HoldType is { } h ? BoulderHoldTypes.Slug(h) : null,
    };
}
