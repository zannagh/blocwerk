// <copyright file="WallMarkerPlanner.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared.MarkerPlanner;
using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The marker planner: the owner draws the wall as an unfolded net, states the photo distance, gets a
/// suggested decodable marker layout, adjusts it, and exports the JSON (kept with every photo dump) and
/// the printable PDF. Wall admins only and never on a kiosk tablet — <see cref="IMarkerPlanService"/>
/// refuses both on save; the page just doesn't offer an editor that would fail at the end.
/// </summary>
public partial class WallMarkerPlanner
{
    private readonly MarkerGenerationOptions options = MarkerGenerationOptions.Default;
    private Guid loadedWallId;
    private string? wallName;
    private string? blockedMessage;
    private bool hadSavedPlan;
    private bool hasGeometry;
    private MarkerPlan? plan;
    private NetGeometry? net;
    private NetCanvasModel? canvas;
    private IReadOnlyList<PlanIssue> issues = [];
    private int? selectedSegment;
    private int? selectedMarker;
    private bool addMode;
    private bool dirty;
    private bool busy;
    private bool confirmRegenerate;
    private string? message;
    private string? failure;
    private string? markerMessage;
    private IReadOnlyList<string> importErrors = [];
    private MarkerCaptureBaseline? captureBaseline;
    private MarkerPlanChanges? changes;
    private IReadOnlyList<MarkerPlanRevisionInfo> revisions = [];

    /// <summary>The wall being planned.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    [Inject]
    private IMarkerPlanService Plans { get; set; } = default!;

    [Inject]
    private IWallGlyphService Glyphs { get; set; } = default!;

    [Inject]
    private WallCacheState Cache { get; set; } = default!;

    [Inject]
    private ICurrentUserService CurrentUser { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private ILogger<WallMarkerPlanner> Logger { get; set; } = default!;

    private PlanSegment? SelectedSegmentModel => plan?.Segments.FirstOrDefault(s => s.Index == selectedSegment);

    private PlanMarker? SelectedMarkerModel => plan?.Markers.FirstOrDefault(m => m.Id == selectedMarker);

    private bool HasErrors => issues.Any(i => i.Severity == PlanIssueSeverity.Error);

    // Route data is loaded here, not in OnInitializedAsync: enhanced navigation between two walls keeps
    // this component alive and only swaps WallId.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId)
        {
            return;
        }

        loadedWallId = WallId;
        ResetState();
        if (KioskContext.IsKiosk)
        {
            blockedMessage = "Marker plans are made from your own device, not from this wall tablet.";
            return;
        }

        try
        {
            await LoadAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Marker planner could not load wall {WallId}", WallId);
            blockedMessage = "This wall could not be loaded.";
        }
    }

    private void ResetState()
    {
        (plan, net, canvas, wallName, blockedMessage) = (null, null, null, null, null);
        (selectedSegment, selectedMarker, addMode, dirty, confirmRegenerate) = (null, null, false, false, false);
        (message, failure, markerMessage, importErrors, issues) = (null, null, null, [], []);
        (captureBaseline, changes, revisions) = (null, null, []);
    }

    private async Task LoadAsync()
    {
        var wall = await Cache.GetWallAsync(WallId);
        if (wall is null)
        {
            blockedMessage = "Wall not found.";
            return;
        }

        wallName = wall.Name;
        var user = await CurrentUser.GetCurrentUserAsync();
        var isAdmin = wall.OwnerId == user.Id || wall.Members.Any(m => m.UserId == user.Id && m.Role == WallRole.Admin);
        if (!isAdmin)
        {
            blockedMessage = "Only the wall's admins can plan its markers.";
            return;
        }

        var saved = await Plans.GetPlanAsync(WallId);
        hadSavedPlan = saved is not null;
        hasGeometry = await Glyphs.GetActiveGeometryAsync(WallId) is not null;
        captureBaseline = await Plans.GetCaptureBaselineAsync(WallId);
        revisions = await Plans.GetRevisionsAsync(WallId);
        SetPlan(saved ?? PlanSegmentEdits.NewPlan(), markDirty: saved is null);
        selectedSegment = plan!.Segments.FirstOrDefault(s => s.AttachedTo is null)?.Index;
    }

    /// <summary>Takes a new plan: re-lays the net, re-validates, and drops selections that no longer exist.</summary>
    private void SetPlan(MarkerPlan updated, bool markDirty = true)
    {
        plan = updated;
        net = Plans.ComputeNet(updated);
        canvas = NetCanvasModel.Build(updated, net, options);
        issues = Plans.Validate(updated);
        changes = captureBaseline?.CompareWith(updated.Markers);
        dirty |= markDirty;
        confirmRegenerate = false;
        if (selectedSegment is { } s && updated.Segments.All(x => x.Index != s))
        {
            selectedSegment = null;
        }

        if (selectedMarker is { } m && updated.Markers.All(x => x.Id != m))
        {
            selectedMarker = null;
        }
    }

    private void SelectSegment(int index)
    {
        selectedSegment = index;
        selectedMarker = null;
        markerMessage = null;
    }

    private void SelectMarker(int id)
    {
        selectedMarker = id;
        markerMessage = null;
        if (plan?.Markers.FirstOrDefault(m => m.Id == id) is { } marker)
        {
            selectedSegment = marker.Segment;
        }
    }

    private void HandleCanvasClick(CanvasClick click)
    {
        if (plan is null || net is null || click.Target < 0)
        {
            return;
        }

        if (!addMode)
        {
            SelectSegment(click.Target);
            return;
        }

        var target = net.Segments.First(s => s.Index == click.Target);
        var local = PlannerGeometry.NetToSegment(target, click.NetX, click.NetY);
        SetPlan(PlanMarkerEdits.Add(plan, click.Target, local, out var id));
        addMode = false;
        SelectMarker(id);
    }

    private void OnMarkerDrop(CanvasClick drop)
    {
        if (plan is null || net is null)
        {
            return;
        }

        SetPlan(PlanMarkerEdits.MoveToNet(plan, net, drop.Target, drop.NetX, drop.NetY));
        SelectMarker(drop.Target);
    }

    private void OnSegmentsChanged((MarkerPlan Plan, int? Select) change)
    {
        SetPlan(change.Plan);
        if (change.Select is { } index)
        {
            SelectSegment(index);
        }
    }

    private void OnSegmentEdited(PlanSegment segment) => SetPlan(PlanSegmentEdits.UpdateSegment(plan!, segment));

    private void OnPhotoChanged(PhotoSetup photo) => SetPlan(plan! with { Photo = photo });

    private void OnPrintChanged(PrintOptions print) => SetPlan(plan! with { Print = print });

    private void OnMarkerEdited((int OldId, PlanMarker Marker) change)
    {
        markerMessage = null;
        if (change.OldId != change.Marker.Id && plan!.Markers.Any(m => m.Id == change.Marker.Id))
        {
            markerMessage = $"Id {change.Marker.Id} is already used by another marker.";
            return;
        }

        SetPlan(PlanMarkerEdits.Replace(plan!, change.OldId, change.Marker));
        selectedMarker = change.Marker.Id;
    }

    private void DeleteMarker(int id)
    {
        SetPlan(PlanMarkerEdits.Delete(plan!, id));
        selectedMarker = null;
    }

    private void PickIssue(PlanIssue issue)
    {
        if (issue.MarkerId is { } id && plan!.Markers.Any(m => m.Id == id))
        {
            SelectMarker(id);
        }
        else if (issue.Segment is { } segment)
        {
            SelectSegment(segment);
        }
    }

    private string? SegmentName(int index) => plan?.Segments.FirstOrDefault(s => s.Index == index)?.Name;

    private double EstimatedPx(int id) => net?.Markers.FirstOrDefault(m => m.Id == id)?.EstimatedPx ?? 0;
}
