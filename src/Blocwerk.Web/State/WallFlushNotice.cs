// <copyright file="WallFlushNotice.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Web.State;

/// <summary>
/// The notice after a flushed wall batch: what was saved, and each boulder the save retired (made historic) or the
/// "changed" marks flagged, by name, linking to its page so the user can review it or revise it back.
/// </summary>
public static class WallFlushNotice
{
    /// <summary>The sentence; it ends with a colon when links follow.</summary>
    public static string Text(IReadOnlyCollection<AffectedBoulder> retired, IReadOnlyCollection<AffectedBoulder> flagged)
    {
        var parts = new List<string>();
        if (retired.Count > 0)
        {
            parts.Add($"{retired.Count} boulder(s) were retired by the deleted holds");
        }

        if (flagged.Count > 0)
        {
            parts.Add($"{flagged.Count} flagged for review by the \u201cchanged\u201d marks");
        }

        return parts.Count == 0
            ? "Your unsaved hold edits were saved before an update."
            : $"Your unsaved hold edits were saved before an update; {string.Join(", and ", parts)}. Open each to review it or revise it back:";
    }

    /// <summary>
    /// One link per boulder, to its page: a retired one offers Revise there (for whoever may revise it) to bring it back
    /// onto the current holds, a flagged one is reviewed there.
    /// </summary>
    public static List<NoticeLink> Links(Guid wallId, IEnumerable<AffectedBoulder> retired, IEnumerable<AffectedBoulder> flagged) =>
    [
        .. retired.Select(b => new NoticeLink($"{b.Name} (retired)", $"/walls/{wallId}/boulders/{b.Id}")),
        .. flagged.Select(b => new NoticeLink($"{b.Name} (review)", $"/walls/{wallId}/boulders/{b.Id}")),
    ];

    /// <summary>
    /// The retired list from what the deletes actually reported (each once, by name), and the flagged list without
    /// any boulder that was retired anyway.
    /// </summary>
    public static (List<AffectedBoulder> Retired, List<AffectedBoulder> Flagged) From(
        IEnumerable<Blocwerk.Core.Services.RetiredBoulder> retiredBySave, IEnumerable<AffectedBoulder> flagged)
    {
        var retired = retiredBySave
            .GroupBy(b => b.Id)
            .Select(g => new AffectedBoulder(g.Key, g.First().Name))
            .OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var ids = retired.Select(b => b.Id).ToHashSet();
        return (retired, flagged.Where(b => !ids.Contains(b.Id)).ToList());
    }
}
