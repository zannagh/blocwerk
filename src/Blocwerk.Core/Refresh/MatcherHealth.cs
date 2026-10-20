// <copyright file="MatcherHealth.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Whether the matcher really ran before the quick update records its defaults. The step-by-step review fails soft (a banner,
/// every old hold carried where it was), but the quick update summed that up as a success: with the native image library
/// missing, The Attic's run reported "0 refound, 1128 kept" and went on to the confirm screen. A matcher that could not start,
/// or that found none of a panel's many old holds again, fails the run instead, with what went wrong.
/// </summary>
public static class MatcherHealth
{
    /// <summary>From this many old holds on a panel, finding none of them again is a failure, not a wall that changed.</summary>
    public const int MinOldHolds = 25;

    /// <summary>Throws when the matched session cannot be trusted; does nothing for a full revision (nothing old is matched).</summary>
    /// <param name="session">The matched session.</param>
    /// <param name="recordedUnaligned">
    /// Panels an earlier run of this update already recorded as unaligned: the review was shown that and went on, so they do
    /// not stop the run again.
    /// </param>
    /// <param name="panels">Whether the per-panel check runs (the apply only checks that the matcher runs at all).</param>
    /// <exception cref="UserFacingException">The matcher could not start, or found no old hold again on a panel with many.</exception>
    public static void EnsureUsable(BigUpdateSession session, IReadOnlySet<Guid>? recordedUnaligned = null, bool panels = true)
    {
        if (session.FullRevision)
        {
            return;
        }

        if (session.AutoMatchStatus == AutoMatchStatus.Unavailable)
        {
            throw new UserFacingException(
                "The hold matcher could not start on this server (its image library did not load"
                + (string.IsNullOrWhiteSpace(session.AutoMatchMessage) ? string.Empty : $": {session.AutoMatchMessage}")
                + "), so no old hold could be found on the new photos and nothing was changed. Ask the server's admin to install "
                + "the native OpenCV library (libOpenCvSharpExtern) for this platform, then run the update again.");
        }

        if (panels && UnmatchedPanel(session, recordedUnaligned) is { } panel)
        {
            throw new UserFacingException(
                $"The matcher found none of the {Physical(panel).Count()} old holds of the {PanelPositionName.Describe(panel.Col, panel.Row)} "
                + "panel on its new photo"
                + (string.IsNullOrWhiteSpace(session.AutoMatchMessage) ? string.Empty : $" ({session.AutoMatchMessage})")
                + ". Carrying them all where they were would not be an update, so nothing was changed. If the wall was reset, start "
                + "the update again as a full revision (every hold is then new). Otherwise retake that panel's photo from about where "
                + "the previous one was taken, so both show the same part of the wall, and run the update again; or open the full "
                + "review of this update and map the holds by hand.");
        }
    }

    /// <summary>The first panel with at least <see cref="MinOldHolds"/> physical old holds of which none was found again.</summary>
    private static CarriedPanelOldHolds? UnmatchedPanel(BigUpdateSession session, IReadOnlySet<Guid>? recordedUnaligned)
    {
        var refound = session.Carryover.Select(c => c.OldHoldId).ToHashSet();
        return (session.CarriedPanels ?? []).FirstOrDefault(p =>
            recordedUnaligned?.Contains(p.StagedPanelId) != true
            && Physical(p).Count() >= MinOldHolds
            && !Physical(p).Any(h => refound.Contains(h.Id)));
    }

    /// <summary>The panel's old holds that are on the photo: virtual holds are never found again.</summary>
    private static IEnumerable<PanelHold> Physical(CarriedPanelOldHolds panel) => panel.OldHolds.Where(h => !h.IsVirtual);
}
