// <copyright file="BoulderRevise.Prefill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Data.Common;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The Revise editor's pre-selection and its cross-panel awareness. The pre-selection comes from
/// <see cref="IBoulderService.GetRevisePrefillAsync"/> (hold lineage), never from raw X/Y. Selections on a panel
/// other than the one shown are counted, offered as jump links, and must be looked at before the revision saves.
/// </summary>
public partial class BoulderRevise
{
    // Pre-selected holds that are only a guess (position fallback) or whose lineage says the hold changed,
    // keyed to the exact entry instance the prefill created. The picker replaces an entry with a NEW instance
    // whenever the user classifies the hold, so a differing instance means "the user has dealt with it".
    private readonly Dictionary<Guid, BoulderHoldInput> _guessedEntries = [];

    // Which panel each hold draws on and which panels the reviser has had on screen. Saving is held back while a
    // selected hold sits on a panel never seen.
    private RevisePanelTracker _panelTracker = new([], []);

    // The boulder's own holds that map to no current hold (deleted, or nothing to match), shown as a note.
    private int _unmappedCount;

    // Lineage picks whose hold was later split with the split tool; only one part is ticked.
    private int _splitCount;

    private IReadOnlySet<Guid> GuessedHoldIds => _guessedEntries.Keys.ToHashSet();

    /// <summary>Clears everything the previous boulder left behind, so a new BoulderId starts from nothing.</summary>
    private void ResetPrefillState()
    {
        _guessedEntries.Clear();
        _panelTracker = new([], []);
        _unmappedCount = 0;
        _splitCount = 0;
    }

    /// <summary>Loads the lineage-based pre-selection and picks the panel to open on.</summary>
    private async Task ApplyPrefillAsync()
    {
        RevisePrefill? prefill;
        try
        {
            prefill = await BoulderService.GetRevisePrefillAsync(BoulderId, _displayHolds, _panels);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException or TimeoutException or UnauthorizedAccessException)
        {
            // The page still opens: the fallback below carries only the holds that survive unchanged.
            Logger.LogWarning(ex, "Revise pre-selection by hold lineage failed for boulder {BoulderId}", BoulderId);
            prefill = null;
        }

        if (prefill is null)
        {
            // Without lineage only an identity carry is certain; nothing is guessed.
            var live = _displayHolds.Select(h => h.Id).ToHashSet();
            _selectedHolds = _boulder!.BoulderHolds
                .Where(bh => live.Contains(bh.HoldId))
                .Select(bh => new BoulderHoldInput(bh.HoldId, bh.Type, bh.Usage))
                .ToList();
            _currentPanel = StartPanel(null);
            return;
        }

        _selectedHolds = [];
        foreach (var pick in prefill.Picks)
        {
            var entry = new BoulderHoldInput(pick.HoldId, pick.Type, pick.Usage);
            _selectedHolds.Add(entry);
            if (pick.Source is RevisePickSource.Guessed or RevisePickSource.LineageChanged)
            {
                _guessedEntries[pick.HoldId] = entry;
            }
        }

        _unmappedCount = prefill.UnmappedHoldIds.Count;
        _splitCount = prefill.SplitHoldIds.Count;
        _currentPanel = StartPanel(prefill.StartPanelId);
    }

    // The panel the editor opens on: the one carrying most of the PRE-SELECTED current holds (the old rows'
    // panel ids belong to retired panels and never match a live one), else the centre, else any panel.
    private WallPanelInfo? StartPanel(Guid? preferred)
    {
        return (preferred is { } id ? _panels.FirstOrDefault(p => p.Id == id) : null)
            ?? _panels.FirstOrDefault(p => p is { Col: 0, Row: 0 })
            ?? _panels.FirstOrDefault();
    }

    private void OnSelectionChanged(List<BoulderHoldInput> selection)
    {
        _selectedHolds = selection;

        // A hold added in the picker (a new virtual hold) lives only in the picker's list: record the panel it was
        // placed on and keep it, so it is counted on that panel and survives a panel switch.
        foreach (var added in _pickerHolds.Where(h => !_panelTracker.Knows(h.Id)).ToList())
        {
            _panelTracker.AddHold(added, _currentPanel?.Id);
            _displayHolds.Add(added);
        }

        foreach (var (holdId, entry) in _guessedEntries.ToList())
        {
            if (!selection.Any(s => ReferenceEquals(s, entry)))
            {
                _guessedEntries.Remove(holdId);
            }
        }
    }

    private IEnumerable<Guid> SelectedIds => _selectedHolds.Select(s => s.HoldId);

    /// <summary>Selected holds the shown panel does not draw, per panel they are on.</summary>
    private List<(WallPanelInfo Panel, int Count)> SelectionsOnOtherPanels() => _currentPanel is not { } current
        ? []
        : _panelTracker.OffScreen(SelectedIds, current.Id);

    /// <summary>The first panel holding a selected hold the reviser has not had on screen yet, if any.</summary>
    private (WallPanelInfo Panel, int Count)? FirstUnseenSelection() => _currentPanel is null
        ? null
        : _panelTracker.FirstUnseen(SelectedIds);

    /// <summary>
    /// The refusal the offline submit path shows (data-bw-boulder-blocked on the save button) while a selection sits
    /// on a panel never seen: offline the circuit is down, so the Blazor check in <see cref="OnSaveClicked"/> cannot run.
    /// </summary>
    private string? OfflineSaveBlock() => FirstUnseenSelection() is { } unseen
        ? $"{unseen.Count} selected hold(s) on {PanelLabel(unseen.Panel)} have not been looked at. Reconnect and check them before saving."
        : null;

    private string PanelLabel(WallPanelInfo panel) => $"Panel {_panels.IndexOf(panel) + 1}/{_panels.Count}";

    private int GuessedSelectedCount => _guessedEntries.Count;

    private void ShowPanel(WallPanelInfo panel)
    {
        _currentPanel = panel;
        _panelTracker.MarkSeen(panel.Id);
        RefreshPickerHolds();
    }

    // The save button: while a selected hold sits on a panel the reviser has not looked at, it shows that
    // panel instead of saving, so nothing off screen is ever saved unseen.
    private async Task OnSaveClicked()
    {
        if (FirstUnseenSelection() is { } unseen)
        {
            ShowPanel(unseen.Panel);
            return;
        }

        await Submit();
    }

    private string SaveLabel()
    {
        if (_isSubmitting)
        {
            return "Saving...";
        }

        if (FirstUnseenSelection() is { } unseen)
        {
            return $"Check {unseen.Count} selected on {PanelLabel(unseen.Panel)} first";
        }

        var other = SelectionsOnOtherPanels().Sum(x => x.Count);
        var where = other > 0 ? $", {other} on other panels" : string.Empty;
        return _isDraft
            ? $"Save Draft ({_selectedHolds.Count} holds{where})"
            : $"Save Revision ({_selectedHolds.Count} holds{where})";
    }
}
