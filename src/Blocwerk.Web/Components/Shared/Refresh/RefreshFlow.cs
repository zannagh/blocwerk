// <copyright file="RefreshFlow.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>
/// What the update page says about a run above its screens: which of the header's steps it is at, the one line about
/// what is being worked on right now, the headline numbers of the check, and the order of its cards. Pure.
/// </summary>
internal static class RefreshFlow
{
    /// <summary>The header's step for a run in this state (none yet is the first step).</summary>
    public static UpdateStep StepOf(WallRefreshStatus? status) => status switch
    {
        null or WallRefreshStatus.Uploading or WallRefreshStatus.Sorting or WallRefreshStatus.ReadyToStart => UpdateStep.Photos,
        WallRefreshStatus.Applying or WallRefreshStatus.Done => UpdateStep.Apply,
        _ => UpdateStep.Check,
    };

    /// <summary>
    /// The one status line while something is being worked on: the step that is running, with its detail, else the 3D
    /// capture's stage; null when nothing is running.
    /// </summary>
    public static string? StatusLine(WallRefreshView view)
    {
        if (view.SummaryUpdating)
        {
            return "Updating the summary with your answer.";
        }

        if (view.Check3DPending)
        {
            return "The new 3D model is ready. Checking the new detections against it; this takes a minute or two.";
        }

        if (view.Steps.FirstOrDefault(s => s.State == RefreshStepState.Running && (view.IsWorking || s.Key != RefreshTimeline.Upload)) is { } running)
        {
            var capture = running.Key == RefreshTimeline.Capture && view.Capture is { IsRunning: true } c
                ? $"{c.Stage ?? "working"} ({(int)Math.Round(c.Progress * 100)} %)"
                : null;
            var detail = capture ?? running.Detail;
            return detail is null ? $"{running.Title}…" : $"{running.Title}: {detail}";
        }

        return view.IsWorking ? "Working…" : null;
    }

    /// <summary>The check's headline numbers, in the order they are shown; a full revision has its own three.</summary>
    public static IReadOnlyList<RefreshNumber> Numbers(RefreshSummary s)
    {
        if (s.FullRevision is { } revision)
        {
            return
            [
                new(revision.HoldsCreated, "new holds"),
                new(revision.OldHoldsRetired, "old holds retired", true),
                new(revision.BouldersArchived.Count, "boulders archived", true),
            ];
        }

        return
        [
            new(s.Refound, "found again"),
            new(s.NewHolds, "new"),
            new(s.KeptInPlace, "not found", s.KeptInPlace > 0),
            new(s.ReplacedHolds, "replaced", s.ReplacedHolds > 0),
            new(s.BouldersOnKeptHolds, "boulders affected", s.BouldersOnKeptHolds > 0),
        ];
    }

    /// <summary>The cards of the check in the order the deck asks about them: what boulders use first, then by what is at stake.</summary>
    public static IReadOnlyList<RefreshCheck> Queue(IReadOnlyList<RefreshCheck> checks) =>
        checks
            .Where(c => c.Status == UpdateExceptionStatus.Pending)
            .OrderBy(c => c.Boulders > 0 ? 0 : 1)
            .ThenBy(c => Rank(c.Kind))
            .ThenByDescending(c => c.Boulders)
            .ToList();

    /// <summary>The Apply button, saying how many cards are still open.</summary>
    public static string ApplyLabel(int open, bool fullRevision)
    {
        var what = fullRevision ? "Apply: start the wall over" : "Apply update";
        return open == 0 ? what : $"{what} — {open} open";
    }

    /// <summary>What applying does, asked before it is done: open cards keep their hold, and nothing is undone from the app.</summary>
    public static string ApplyQuestion(RefreshSummary s)
    {
        if (s.FullRevision is { } revision)
        {
            return $"Start the wall over now? {revision.OldHoldsRetired} old holds are retired, {revision.HoldsCreated} new holds are created and "
                + $"{PlainCopy.Plural(revision.BouldersArchived.Count, "boulder")} {(revision.BouldersArchived.Count == 1 ? "is" : "are")} archived. "
                + "This cannot be undone from the app.";
        }

        var open = s.ChecksOpen switch
        {
            0 => "Every card is answered.",
            1 => "1 card is still open: its hold stays, marked for review, and comes up again in the look-over afterwards.",
            _ => $"{s.ChecksOpen} cards are still open: their holds stay, marked for review, and come up again in the look-over afterwards.",
        };
        return $"Apply the update to the wall now? {open} The new photos and holds go live; this cannot be undone from the app.";
    }

    /// <summary>What discarding the run loses, asked before it is done.</summary>
    public static string DiscardQuestion(WallRefreshView? view)
    {
        if (view is { Status: WallRefreshStatus.ReadyToApply })
        {
            return "Discard this panel update? The panels keep their current photos and holds.";
        }

        var photos = view?.Photos.Count ?? 0;
        var videos = view?.Videos.Count ?? 0;
        var parts = new List<string>();
        if (photos > 0)
        {
            parts.Add(photos == 1 ? "1 photo" : $"{photos} photos");
        }

        if (videos > 0)
        {
            parts.Add(videos == 1 ? "1 video" : $"{videos} videos");
        }

        return parts.Count == 0
            ? "Discard this update?"
            : $"Discard {string.Join(" and ", parts)}? They are deleted; the wall stays as it is.";
    }

    private static int Rank(UpdateExceptionKind kind) => kind switch
    {
        UpdateExceptionKind.ReplacedByOther => 0,
        UpdateExceptionKind.PossiblyRemoved => 1,
        UpdateExceptionKind.LowConfidenceMatch => 2,
        UpdateExceptionKind.MatchedToHandPlaced => 3,
        UpdateExceptionKind.HandPlacedAmbiguous => 4,
        _ => 5,
    };
}
