// <copyright file="PanelOverlapStepper.Suggested.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The per-hold keep for this panel's detections the service discards by default (see
/// <see cref="SuggestedRemovals"/>): a view of just those holds on the new photo, where a tap keeps or
/// discards one. Kept ones leave <c>_removed</c>, so they promote like any other hold.
/// </summary>
public partial class PanelOverlapStepper
{
    private const string KeptColor = "var(--status-success)";
    private const string DiscardedColor = "var(--status-danger)";

    private bool suggestedMode;

    /// <summary>This panel's holds the service suggested discarding (still on the panel).</summary>
    private List<PanelHold> SuggestedHere =>
        (SuggestedRemovals ?? []).Where(_stagedHolds.ContainsKey).Select(id => _stagedHolds[id]).ToList();

    private int SuggestedKeptCount => SuggestedHere.Count(h => !_removed.Contains(h.Id));

    private Dictionary<Guid, string> SuggestedColors =>
        SuggestedHere.ToDictionary(h => h.Id, h => _removed.Contains(h.Id) ? DiscardedColor : KeptColor);

    private void EnterSuggested()
    {
        _touchup.Reset();
        suggestedMode = true;
    }

    private void LeaveSuggested()
    {
        suggestedMode = false;
        _refocus = true;
    }

    private async Task ToggleSuggestedAsync(Guid holdId)
    {
        if (!_removed.Remove(holdId))
        {
            _removed.Add(holdId);
        }

        await ReportProgressAsync();
    }

    private async Task SetAllSuggestedAsync(bool keep)
    {
        foreach (var hold in SuggestedHere)
        {
            if (keep)
            {
                _removed.Remove(hold.Id);
            }
            else
            {
                _removed.Add(hold.Id);
            }
        }

        await ReportProgressAsync();
    }
}
