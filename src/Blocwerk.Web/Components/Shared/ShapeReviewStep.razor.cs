// <copyright file="ShapeReviewStep.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The shape review of the big-wall update. Loads the recognised shapes (least confident first), filters
/// them by a confidence ceiling, and writes each verdict straight to the session through
/// <see cref="IWallUpdateShapeService"/> — the same calls the REST API makes. Nothing touches a hold here.
/// </summary>
public partial class ShapeReviewStep
{
    private const int PageSize = 48;

    private List<ShapeProposalInfo>? _all;
    private double _below = 1.01;
    private double _acceptAt = 0.7;
    private int _shown = PageSize;
    private bool _busy;
    private string? _error;
    private ShapeProposalInfo? _adjusting;
    private (Guid, Guid?)? _loadedFor;

    [Parameter]

    public Guid WallId { get; set; }

    [Parameter]

    public Guid? SessionId { get; set; }

    [Parameter]

    public EventCallback OnContinue { get; set; }

    [Parameter]

    public EventCallback OnBack { get; set; }

    /// <summary>Whether the adjust overlay is open (the wizard's keys stand down while it is).</summary>
    public bool IsAdjusting => _adjusting is not null;

    [Inject]
    private IWallUpdateShapeService Shapes { get; set; } = default!;

    private List<ShapeProposalInfo> Visible => (_all ?? []).Where(p => p.Confidence < _below).ToList();

    private int BulkCount => (_all ?? []).Count(p =>
        p.Decision == ShapeReviewDecision.Pending && p.Shape is not null && p.Confidence >= _acceptAt);

    // Enhanced-nav rule: load route-ish data here, once per (wall, session).
    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor == (WallId, SessionId))
        {
            return;
        }

        _loadedFor = (WallId, SessionId);
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _all = (await Shapes.GetProposalsAsync(WallId)).ToList();
            _error = null;
        }
        catch (Exception ex)
        {
            _all ??= [];
            _error = $"Could not load the recognised shapes: {ex.Message}";
        }
    }

    private async Task DecideAsync(ShapeProposalInfo proposal, ShapeReviewDecision decision)
    {
        // Pressing the active verdict again takes it back, so a mis-tap is one tap to undo.
        var next = proposal.Decision == decision ? ShapeReviewDecision.Pending : decision;
        await WriteAsync(proposal, new ShapeDecisionRequest(proposal.HoldId, next), p => p with { Decision = next });
    }

    private async Task SaveAdjustedAsync(ShapeProposalInfo proposal, List<ShapePoint> shape)
    {
        _adjusting = null;
        await WriteAsync(
            proposal,
            new ShapeDecisionRequest(proposal.HoldId, ShapeReviewDecision.Adjusted, shape),
            p => p with { Decision = ShapeReviewDecision.Adjusted, AdjustedShape = shape });
    }

    private async Task WriteAsync(ShapeProposalInfo proposal, ShapeDecisionRequest request, Func<ShapeProposalInfo, ShapeProposalInfo> update)
    {
        try
        {
            await Shapes.DecideAsync(WallId, [request], SessionId);
            var index = _all!.FindIndex(p => p.HoldId == proposal.HoldId);
            if (index >= 0)
            {
                _all[index] = update(_all[index]);
            }

            _error = null;
        }
        catch (Exception ex)
        {
            _error = $"Could not save that verdict: {ex.Message}";
        }
    }

    private async Task AcceptAboveAsync()
    {
        _busy = true;
        try
        {
            await Shapes.AcceptAboveAsync(WallId, _acceptAt, SessionId);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _error = $"Could not accept the shapes: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetBelow(object? value)
    {
        _below = Parse(value, _below);
        _shown = PageSize;
    }

    private void SetAcceptAt(object? value) => _acceptAt = Parse(value, _acceptAt);

    private static double Parse(object? value, double fallback) =>
        double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private int Count(Func<ShapeProposalInfo, bool> predicate) => (_all ?? []).Count(predicate);

    private static string Pct(double v) => v > 1 ? "100%" : $"{v * 100:0}%";

    private string StagedPhotoUrl(Guid panelId) => $"/api/walls/{WallId}/panels/{panelId}/staged-photo";
}
