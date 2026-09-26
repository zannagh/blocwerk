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
    /// <returns>One sentence, or null.</returns>
    public static string? Summary(CaptureFollowUpRecord record)
    {
        var done = record.Steps
            .Where(s => s.Outcome == CaptureFollowUpOutcome.Done && !string.IsNullOrWhiteSpace(s.Summary))
            .Select(s => s.Summary)
            .ToList();
        return done.Count == 0 ? null : Sentence(string.Join(", ", done));
    }

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
