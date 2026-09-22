// <copyright file="WallViewPreference.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The member's wall-editor overlay preferences — the "view" toolbar group: hold names, the wall
/// border, the changed-hold highlight and the missing-information overlay's criteria.
/// <para>
/// Stored as one plain cookie by <c>wwwroot/js/prefs.js</c> (<c>bwPrefs.getWallView</c> /
/// <c>setWallView</c>), exactly like the toolbar placement: a cookie rather than localStorage so
/// the server can read it on the prerender pass and the editor's FIRST paint already has the right
/// overlays on, with no flash of the wrong state.
/// </para>
/// <para>
/// Only genuine VIEW preferences live here. The hold-usage toggle deliberately does not: it parks
/// the active tool and refetches the usage map, so it is mid-task state, not a setting.
/// </para>
/// </summary>
/// <param name="ShowNames">Whether hold name labels are drawn.</param>
/// <param name="ShowBorder">Whether the wall border outline is drawn.</param>
/// <param name="ShowChanges">Whether holds marked changed are highlighted.</param>
/// <param name="IncompleteCriteria">Which missing-information criteria the "?" overlay badges.</param>
/// <param name="Tool">The tool the editor was last left in, if it is one worth reopening in.</param>
public readonly record struct WallViewPreference(
    bool ShowNames,
    bool ShowBorder,
    bool ShowChanges,
    HoldCompletenessCriteria IncompleteCriteria,
    HoldTouchupTool Tool = HoldTouchupTool.None)
{
    /// <summary>
    /// The tools the editor may REOPEN in. Two kinds are deliberately missing.
    /// <para>
    /// Destructive ones (<see cref="HoldTouchupTool.Delete"/>, the two Mark verdicts): coming back to
    /// a wall already holding the delete tool means the first tap on a hold destroys it, and nothing
    /// about reopening an editor says "I meant to keep deleting".
    /// </para>
    /// <para>
    /// Contextual ones (<see cref="HoldTouchupTool.Merge"/>, <see cref="HoldTouchupTool.MakeActual"/>,
    /// <see cref="HoldTouchupTool.JoinVirtual"/>): they only mean anything mid staged-update, so on an
    /// ordinary wall they would restore a tool whose option row has nothing to say.
    /// </para>
    /// Anything outside this set simply falls back to the editor's own default.
    /// </summary>
    private static readonly HoldTouchupTool[] RestorableTools =
    [
        HoldTouchupTool.None,
        HoldTouchupTool.Add,
        HoldTouchupTool.Move,
        HoldTouchupTool.Pipette,
        HoldTouchupTool.Shape,
        HoldTouchupTool.ShapeTilt,
        HoldTouchupTool.Paint,
        HoldTouchupTool.Name,
        HoldTouchupTool.Border,
    ];

    /// <summary>The cookie prefs.js writes. Also listed on the privacy page.</summary>
    public const string CookieName = "blocwerk-wall-view";

    /// <summary>
    /// What a member who has never touched the toggles sees: border and change highlight on, names
    /// off, no missing-information badges. Identical to the editor's own field initialisers — keep
    /// the two in step, because an unset cookie must not change what the editor looks like.
    /// </summary>
    public static WallViewPreference Default { get; } = new(
        ShowNames: false,
        ShowBorder: true,
        ShowChanges: true,
        HoldCompletenessCriteria.None,
        HoldTouchupTool.None);

    /// <summary>Whether the editor may open straight into <paramref name="tool"/>.</summary>
    public static bool IsRestorable(HoldTouchupTool tool) => Array.IndexOf(RestorableTools, tool) >= 0;

    /// <summary>
    /// Reads a stored value. The format is a dot-separated list of one-letter keys with an integer
    /// value (<c>n1.b0.c1.i3</c>) — no character in it is escaped by <c>encodeURIComponent</c>, so
    /// the JS-written cookie and the raw value ASP.NET hands back off the request are the same
    /// string. Unknown keys are ignored and a missing or unparseable key keeps its default, so an
    /// old, truncated or tampered-with cookie degrades to the default instead of throwing.
    /// </summary>
    public static WallViewPreference Parse(string? stored)
    {
        var result = Default;
        if (string.IsNullOrWhiteSpace(stored))
        {
            return result;
        }

        foreach (var part in stored.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2)
            {
                continue;
            }

            // The tool is the one key carried as a NAME rather than a number, so it is read before
            // the integer gate instead of being dropped by it. By name, never by number: the enum's
            // members are grouped by surface and get reordered as tools are added, and a stale
            // cookie must never come back as a DIFFERENT — possibly destructive — tool than the one
            // that was stored. An unknown or non-restorable name keeps the default.
            if (part[0] == 't')
            {
                if (Enum.TryParse<HoldTouchupTool>(part[1..], ignoreCase: false, out var tool)
                    && IsRestorable(tool))
                {
                    result = result with { Tool = tool };
                }

                continue;
            }

            if (!int.TryParse(
                part.AsSpan(1),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
            {
                continue;
            }

            switch (part[0])
            {
                case 'n':
                    result = result with { ShowNames = value != 0 };
                    break;
                case 'b':
                    result = result with { ShowBorder = value != 0 };
                    break;
                case 'c':
                    result = result with { ShowChanges = value != 0 };
                    break;
                case 'i':
                    result = result with { IncompleteCriteria = ParseCriteria(value) };
                    break;
                default:
                    break;
            }
        }

        return result;
    }

    /// <summary>The cookie value for these preferences.</summary>
    public string ToCookieValue() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"n{(ShowNames ? 1 : 0)}.b{(ShowBorder ? 1 : 0)}.c{(ShowChanges ? 1 : 0)}.i{(int)IncompleteCriteria}.t{(IsRestorable(Tool) ? Tool : HoldTouchupTool.None)}");

    /// <summary>
    /// Keeps only bits the enum actually defines, so a stale cookie written by a future (or edited)
    /// client can never switch on a criterion this build does not understand.
    /// </summary>
    private static HoldCompletenessCriteria ParseCriteria(int value)
    {
        const int known = (int)(HoldCompletenessCriteria.Color | HoldCompletenessCriteria.HandType);
        return (HoldCompletenessCriteria)(value & known);
    }
}
