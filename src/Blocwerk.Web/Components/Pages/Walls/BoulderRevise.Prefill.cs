// <copyright file="BoulderRevise.Prefill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

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

    // Panels the reviser has had on screen. Saving is held back while a selected hold sits on a panel not in here.
    private readonly HashSet<Guid> _seenPanelIds = [];

    // The boulder's own holds that map to no current hold (deleted, or nothing to match), shown as a note.
    private int _unmappedCount;

    private IReadOnlySet<Guid> GuessedHoldIds => _guessedEntries.Keys.ToHashSet();

    private Guid? CentrePanelId => _panels.FirstOrDefault(p => p is { Col: 0, Row: 0 })?.Id;

    /// <summary>Loads the lineage-based pre-selection and picks the panel to open on.</summary>
    private async Task ApplyPrefillAsync()
    {
        RevisePrefill? prefill;
        try
        {
            prefill = await BoulderService.GetRevisePrefillAsync(BoulderId, _displayHolds, _panels);
        }
        catch (Exception)
        {
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
        foreach (var (holdId, entry) in _guessedEntries.ToList())
        {
            if (!selection.Any(s => ReferenceEquals(s, entry)))
            {
                _guessedEntries.Remove(holdId);
            }
        }
    }

    /// <summary>The live panel a selected hold is drawn on; a hold with no panel draws on the centre.</summary>
    private Guid? PanelOfHold(Guid holdId) =>
        _displayHolds.FirstOrDefault(h => h.Id == holdId)?.WallPanelId ?? CentrePanelId;

    /// <summary>Whether a selected hold draws on <paramref name="panelId"/>, itself or through a cross-panel twin.</summary>
    private bool IsVisibleOn(Guid holdId, Guid panelId) =>
        PanelOfHold(holdId) == panelId
        || (_holdTwins?.TryGetValue(holdId, out var twins) == true && twins.Any(t => PanelOfHold(t) == panelId));

    /// <summary>Selected holds the matching panels do not show, grouped by the panel they are on, in panel order.</summary>
    private List<(WallPanelInfo Panel, int Count)> SelectionsHiddenFrom(Func<Guid, bool> shown) => _panels
        .Select(p => (Panel: p, Count: _selectedHolds.Count(s => PanelOfHold(s.HoldId) == p.Id && !shown(s.HoldId))))
        .Where(x => x.Count > 0)
        .ToList();

    /// <summary>Selected holds the shown panel does not draw, per panel they are on.</summary>
    private List<(WallPanelInfo Panel, int Count)> SelectionsOnOtherPanels() => _currentPanel is not { } current
        ? []
        : SelectionsHiddenFrom(holdId => IsVisibleOn(holdId, current.Id));

    /// <summary>The first panel holding a selected hold the reviser has not had on screen yet, if any.</summary>
    private (WallPanelInfo Panel, int Count)? FirstUnseenSelection() => _currentPanel is null
        ? null
        : SelectionsHiddenFrom(holdId => _seenPanelIds.Any(seen => IsVisibleOn(holdId, seen)))
            .Select(x => ((WallPanelInfo Panel, int Count)?)x)
            .FirstOrDefault();

    private string PanelLabel(WallPanelInfo panel) => $"Panel {_panels.IndexOf(panel) + 1}/{_panels.Count}";

    private int GuessedSelectedCount => _guessedEntries.Count;

    private void ShowPanel(WallPanelInfo panel)
    {
        _currentPanel = panel;
        _seenPanelIds.Add(panel.Id);
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
