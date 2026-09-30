// <copyright file="CaptureFollowUpText.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>The chain's record in plain words, for the capture history and the API.</summary>
public static class CaptureFollowUpText
{
    /// <summary>The summary of the steps a correction carried over instead of running them again.</summary>
    public const string KeptFromPreviousVersion = "holds, shapes and volumes carried over from the previous model version";

    /// <summary>
    /// What the chain changed, e.g. "856 holds placed on the 3D model, 653 hold shapes refined from several
    /// photos." Null when it changed nothing worth saying (or has not run).
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="listedProposals">
    /// How many hold proposals the review list shows now, when known: it replaces the count stored when the search ran
    /// (accepted, rejected and covered proposals no longer wait for review). Null keeps the stored text.
    /// </param>
    /// <returns>One sentence, or null.</returns>
    public static string? Summary(CaptureFollowUpRecord record, int? listedProposals) =>
        Summary(record, new CaptureLiveCounts(Proposals: listedProposals));

    /// <summary>
    /// As <see cref="Summary(CaptureFollowUpRecord, int?)"/>, with every count the wall shows now (for a capture of the
    /// active model) in place of the stored one: proposals to review, holds placed from photos, volumes and the holds on them.
    /// A step that reported nothing, or was carried over from a previous model version, keeps its stored text.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <param name="live">The live counts, or null to keep every stored text.</param>
    /// <returns>One sentence, or null.</returns>
    public static string? Summary(CaptureFollowUpRecord record, CaptureLiveCounts? live = null)
    {
        var done = record.Steps
            .Where(s => s.Outcome == CaptureFollowUpOutcome.Done)
            .Select(s => live is null || !Reports(s) ? s.Summary : Live(s, live))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        return done.Count == 0 ? null : Sentence(string.Join(", ", done));
    }

    /// <summary>The hold proposal search ran and reported proposals, so a live count is worth reading for <see cref="Summary(CaptureFollowUpRecord, CaptureLiveCounts?)"/>.</summary>
    /// <param name="record">The record.</param>
    /// <returns>Whether the record says how many holds were proposed.</returns>
    public static bool ReportsProposals(CaptureFollowUpRecord record) =>
        record.Find(FindHoldProposalsFollowUpStep.StepKey) is { Outcome: CaptureFollowUpOutcome.Done } step
        && !string.IsNullOrWhiteSpace(step.Summary);

    /// <summary>The step <paramref name="key"/> ran and reported a count of its own (not one carried from a previous model version).</summary>
    /// <param name="record">The record.</param>
    /// <param name="key">The step's key.</param>
    /// <returns>Whether its stored count would be replaced by a live one.</returns>
    public static bool ReportsCount(CaptureFollowUpRecord record, string key) =>
        record.Find(key) is { Outcome: CaptureFollowUpOutcome.Done } step && Reports(step);

    /// <summary>What went wrong (each failed step) and the record's note, or null when there is nothing to add.</summary>
    /// <param name="record">The record.</param>
    /// <returns>One or two sentences, or null.</returns>
    public static string? Note(CaptureFollowUpRecord record)
    {
        var parts = record.Steps
            .Where(s => s.Outcome == CaptureFollowUpOutcome.Failed)
            .Select(s => Sentence(s.Summary))
            .ToList();
        if (!string.IsNullOrWhiteSpace(record.Note))
        {
            parts.Add(Sentence(record.Note));
        }

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>"1 hold" / "856 holds".</summary>
    /// <param name="count">How many.</param>
    /// <param name="singular">One of them.</param>
    /// <param name="plural">Several.</param>
    /// <returns>The count with its noun.</returns>
    public static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");

    private static bool Reports(CaptureFollowUpEntry step) =>
        !string.IsNullOrWhiteSpace(step.Summary) && step.Summary != KeptFromPreviousVersion;

    private static string Live(CaptureFollowUpEntry step, CaptureLiveCounts live) => step.Key switch
    {
        FindHoldProposalsFollowUpStep.StepKey when live.Proposals is { } n => FindHoldProposalsFollowUpStep.Describe(n),
        PlaceHoldsFollowUpStep.StepKey when live.PlacedFromPhotos is { } n =>
            PlaceHoldsFollowUpStep.DescribeLive(n, live.CarriedFromPhotos, live.Unmeasured),
        DetectVolumesFollowUpStep.StepKey when live.Volumes is { } n => VolumesLive(step.Summary, n, live.HoldsOnVolumes),
        _ => step.Summary,
    };

    private static string VolumesLive(string stored, int volumes, int holds)
    {
        if (volumes == 0)
        {
            return "no visible volume left";
        }

        var text = DetectVolumesFollowUpStep.Describe(volumes, holds);
        return stored.EndsWith(DetectVolumesFollowUpStep.SparseSuffix, StringComparison.Ordinal) ? text + DetectVolumesFollowUpStep.SparseSuffix : text;
    }

    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return trimmed;
        }

        var capitalised = $"{char.ToUpperInvariant(trimmed[0])}{trimmed[1..]}";
        return capitalised.EndsWith('.') ? capitalised : capitalised + ".";
    }
}
