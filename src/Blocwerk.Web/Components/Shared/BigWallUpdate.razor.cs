using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The dedicated big-wall update surface: upload a fresh multi-photo capture, carry the old curated
/// holds (and their boulders) onto the new centre, confirm the overlaps of each neighbour panel, then
/// promote it all live in one go. Orchestrates the phases and the <see cref="IWallBigUpdateService"/>
/// calls; each phase's UI lives in its own component. State here, markup in the .razor.
/// </summary>
public partial class BigWallUpdate : IDisposable
{
    [Inject]
    private IWallBigUpdateService BigUpdate { get; set; } = default!;

    [Inject]
    private CircuitEditActivity EditActivity { get; set; } = default!;

    // The whole carryover flow is unsaved, in-flight wall work (staged panels, carryover decisions,
    // overlap links) until Apply promotes it. Hold a wall-edit busy lease for the component's mounted
    // lifetime so a deploy can't recreate the container mid-flow. Released in Dispose (WallDetail
    // unmounts this component when the flow closes), with the circuit-teardown backstop behind it.
    private IDisposable? _editLease;

    [Parameter] public Guid WallId { get; set; }
    [Parameter] public int CurrentGeneration { get; set; }

    /// <summary>Raised when the user leaves the flow (discarded or finished) so the parent can reload.</summary>
    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>Raised after a successful promote so the parent can refresh the now-updated wall.</summary>
    [Parameter] public EventCallback OnPromoted { get; set; }

    private enum Phase
    {
        Loading,
        ResumePrompt,
        Upload,
        Carryover,
        Neighbours,
        Confirm,
        Working,
        Done,
    }

    private Phase _phase = Phase.Loading;
    private string? _error;

    private BigUpdateSession? _session;
    private CarryoverOutcome? _outcome;
    private readonly List<NeighbourLinkSet> _linkSets = [];
    private int _neighbourIndex;

    protected override async Task OnInitializedAsync()
    {
        _editLease = EditActivity.BeginWallEdit(WallId, userId: null);

        // A prior update may still be in flight (staged panels persisted); offer to resume it.
        try
        {
            _session = await BigUpdate.ResumeAsync(WallId);
            _phase = Phase.ResumePrompt;
        }
        catch (Exception)
        {
            _phase = Phase.Upload;
        }
    }

    private NeighbourOverlap? CurrentNeighbour =>
        _session is not null && _neighbourIndex >= 0 && _neighbourIndex < _session.Neighbours.Count
            ? _session.Neighbours[_neighbourIndex]
            : null;

    // ---- Resume / start --------------------------------------------------------
    private void ResumeExisting() => _phase = Phase.Carryover;

    private async Task DiscardAndRestart()
    {
        await SafeDiscard();
        _session = null;
        _phase = Phase.Upload;
    }

    private async Task OnUpload(IReadOnlyList<BigUpdatePhoto> photos)
    {
        _error = null;
        _phase = Phase.Working;
        try
        {
            _session = await BigUpdate.StartAsync(WallId, photos);
            _phase = Phase.Carryover;
        }
        catch (Exception ex)
        {
            _error = $"Could not start the update: {ex.Message}";
            _phase = Phase.Upload;
        }
    }

    // ---- Phase 1 → Phase 2 -----------------------------------------------------
    private void OnCarryoverContinue(CarryoverOutcome outcome)
    {
        _outcome = outcome;
        _linkSets.Clear();
        _neighbourIndex = 0;

        _phase = (_session?.Neighbours.Count ?? 0) == 0 ? Phase.Confirm : Phase.Neighbours;
    }

    // ---- Phase 2: one neighbour overlap at a time ------------------------------
    private void OnNeighbourConfirm(PanelConfirmation confirmation)
    {
        if (CurrentNeighbour is { } n)
        {
            _linkSets.Add(new NeighbourLinkSet(n.PanelId, confirmation.Links.ToList(), confirmation.RemovedNeighborHoldIds.ToList()));
        }

        AdvanceNeighbour();
    }

    // Skipping a neighbour keeps its panel (it is promoted with the rest) but records no links —
    // it never deletes the staged panel, so no holds are lost.
    private void OnNeighbourSkip()
    {
        if (CurrentNeighbour is { } n)
        {
            _linkSets.Add(new NeighbourLinkSet(n.PanelId, [], []));
        }

        AdvanceNeighbour();
    }

    private void AdvanceNeighbour()
    {
        if (_session is not null && _neighbourIndex < _session.Neighbours.Count - 1)
        {
            _neighbourIndex++;
        }
        else
        {
            _phase = Phase.Confirm;
        }
    }

    // ---- Finish ----------------------------------------------------------------
    private int CarriedCount => _outcome?.Carryover.Count(d => d.Kind == CarryKind.Carried) ?? 0;
    private int MovedCount => _outcome?.Carryover.Count(d => d.Kind == CarryKind.Moved) ?? 0;
    private int RemovedCount => _outcome?.Carryover.Count(d => d.Kind == CarryKind.Removed) ?? 0;
    private int NewKeptCount => _outcome?.AcceptedNewCenterHoldIds.Count ?? 0;
    private int LinkCount => _linkSets.Sum(l => l.Links.Count);

    private async Task Apply()
    {
        if (_outcome is null)
        {
            return;
        }

        _error = null;
        _phase = Phase.Working;
        try
        {
            var confirmation = new BigUpdateConfirmation(
                _outcome.Carryover,
                _outcome.AcceptedNewCenterHoldIds,
                _outcome.RemovedNewCenterHoldIds,
                _linkSets);
            await BigUpdate.PromoteAsync(WallId, confirmation);
            _phase = Phase.Done;
            await OnPromoted.InvokeAsync();
        }
        catch (Exception ex)
        {
            _error = $"Could not apply the update: {ex.Message}";
            _phase = Phase.Confirm;
        }
    }

    private async Task Discard()
    {
        await SafeDiscard();
        await OnClose.InvokeAsync();
    }

    private async Task SafeDiscard()
    {
        try
        {
            await BigUpdate.DiscardAsync(WallId);
        }
        catch (Exception ex)
        {
            _error = $"Discard failed: {ex.Message}";
        }
    }

    private Task Close() => OnClose.InvokeAsync();

    public void Dispose()
    {
        _editLease?.Dispose();
    }
}
