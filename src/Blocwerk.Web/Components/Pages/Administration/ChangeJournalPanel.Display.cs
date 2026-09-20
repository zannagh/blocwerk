// <copyright file="ChangeJournalPanel.Display.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Administration;

/// <summary>
/// The presentation half of <see cref="ChangeJournalPanel"/>: turns a batch summary into the one
/// line an operator can judge — <c>hold-clean-outside-border · Nordwand · 181 holds deleted · by
/// Patrick · 2 hours ago</c> — and answers, in one place, whether a batch may be offered a revert.
/// Split out of the code-behind purely to keep both files small.
/// </summary>
public partial class ChangeJournalPanel
{
    /// <summary>
    /// The single gate the revert control hangs off. Every clause is a rule the service either
    /// cannot undo cleanly or would refuse outright, so the button simply does not exist for it.
    /// </summary>
    private static bool CanOfferRevert(ChangeJournalBatchSummary batch, ChangeJournalRevertPreview preview) =>
        preview.Found
        && batch.Status == ChangeJournalStatus.Recorded
        && batch.SealedAt is not null
        && !batch.IsWallUpdateBatch
        && preview.Status == ChangeJournalStatus.Recorded
        && preview.CanRevert;

    private static bool IsRevertOfSomething(ChangeJournalBatchSummary batch) =>
        batch.Label.StartsWith(RevertLabelPrefix, StringComparison.Ordinal);

    private static string RevertedLabel(ChangeJournalBatchSummary batch) =>
        batch.Label[RevertLabelPrefix.Length..];

    private static string StatusReason(ChangeJournalBatchSummary batch) => batch.Status switch
    {
        ChangeJournalStatus.Reverted =>
            "Already reverted — its effect has been put back once and a batch can only be reverted once.",
        ChangeJournalStatus.Replayed =>
            "Replayed onto another environment — it is no longer the authoritative record here and "
            + "cannot be reverted.",
        _ => string.Empty,
    };

    private static string SummaryLine(ChangeJournalBatchSummary batch)
    {
        var parts = new List<string> { batch.Label, ScopeLabel(batch), DescribeCounts(batch) };
        parts.Add(batch.ActorName is { Length: > 0 } actor ? $"by {actor}" : "system / unknown");
        parts.Add(FormatRelative(batch.CreatedAt));
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    private static string ScopeLabel(ChangeJournalBatchSummary batch)
    {
        if (batch.ScopeKind == ChangeJournalScopeKind.None)
        {
            return "no wall";
        }

        var kind = batch.ScopeKind == ChangeJournalScopeKind.Wall ? "wall" : "boulder";
        return batch.ScopeName is { Length: > 0 } name ? name : $"deleted {kind}";
    }

    /// <summary>"181 holds deleted, 3 boulders changed" — the counts the browser already grouped.</summary>
    private static string DescribeCounts(ChangeJournalBatchSummary batch)
    {
        if (batch.Counts.Count == 0)
        {
            return batch.EntryCount == 0 ? "nothing recorded" : $"{batch.EntryCount} change(s)";
        }

        var described = batch.Counts
            .Take(3)
            .Select(c => $"{c.Count} {Pluralise(Humanise(c.EntityType), c.Count)} {OpVerb(c.Op)}")
            .ToList();

        if (batch.Counts.Count > described.Count)
        {
            described.Add($"and {batch.Counts.Count - described.Count} more kind(s)");
        }

        return string.Join(", ", described);
    }

    private static string OpVerb(ChangeJournalOp op) => op switch
    {
        ChangeJournalOp.Insert => "added",
        ChangeJournalOp.Delete => "deleted",
        _ => "changed",
    };

    /// <summary>Lower-cases an entity type and splits its camel case, so <c>WallPanel</c> reads "wall panel".</summary>
    private static string Humanise(string entityType)
    {
        if (string.IsNullOrEmpty(entityType))
        {
            return "row";
        }

        var builder = new StringBuilder(entityType.Length + 4);
        for (var i = 0; i < entityType.Length; i++)
        {
            if (i > 0 && char.IsUpper(entityType[i]) && !char.IsUpper(entityType[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(char.ToLowerInvariant(entityType[i]));
        }

        return builder.ToString();
    }

    private static string Pluralise(string noun, int count)
    {
        if (count == 1 || noun.EndsWith('s'))
        {
            return noun;
        }

        return noun.EndsWith('y') && noun.Length > 1 && !"aeiou".Contains(noun[^2])
            ? $"{noun[..^1]}ies"
            : $"{noun}s";
    }

    private static string FormatRelative(DateTimeOffset at)
    {
        var elapsed = DateTimeOffset.UtcNow - at;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes} minute(s) ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours} hour(s) ago";
        }

        if (elapsed < TimeSpan.FromDays(14))
        {
            return $"{(int)elapsed.TotalDays} day(s) ago";
        }

        return at.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>The first block of a guid — enough to match against a log line, short enough to read.</summary>
    private static string FormatShortId(Guid? id) =>
        id is { } value ? value.ToString()[..8] : "—";
}
