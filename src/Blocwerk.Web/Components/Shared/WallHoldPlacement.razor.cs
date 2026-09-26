using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for "place existing holds on the 3D model": loads whether it is available and the latest run,
/// and routes place / revert through <see cref="IHoldTexturePlacementService"/>.
/// </summary>
public partial class WallHoldPlacement
{
    private Guid loadedWallId;
    private bool busy;
    private bool confirmRevert;
    private string? message;
    private string? failure;
    private HoldPlacementStatus? status;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: the action is never offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IHoldTexturePlacementService Placement { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallHoldPlacement> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>The run in one sentence, e.g. 861 placed, 0 left alone, 19 not measured.</summary>
    internal static string RunText(HoldPlacementRunInfo run) =>
        WithUnmeasured($"{run.Placed} holds placed, {run.Skipped} left alone, {run.Failed} not measured.", run.Panels);

    /// <summary>One panel line: its counts, then each facet's verdict with the evidence behind it.</summary>
    internal static string PanelText(HoldPlacementPanelSummary p)
    {
        var text = $"{p.Label}: {p.Placed} placed, {p.Skipped} left alone, {p.Failed} failed";
        if (p.Disagreed + p.Unsupported > 0)
        {
            text += $" ({p.Disagreed} where the other photo disagrees, {p.Unsupported} beyond this photo's matches)";
        }

        if (p.Problem is not null)
        {
            text += $" ({p.Problem})";
        }

        var facets = p.Facets.Select(f => f.Accepted
            ? string.Create(CultureInfo.InvariantCulture, $"facet {f.FacetId} ✓ {f.Inliers} inliers, {f.Coverage:P0}, {f.RmsMm:F1} mm")
            : $"facet {f.FacetId} ✗ {f.Reason}");
        return p.Facets.Count == 0 ? text : $"{text} — {string.Join("; ", facets)}";
    }

    /// <summary>The sentence plus, when the run left holds unmeasured on purpose, why (<see cref="HoldPlacementUnmeasured"/>).</summary>
    internal static string WithUnmeasured(string text, IEnumerable<HoldPlacementPanelSummary> panels) =>
        HoldPlacementUnmeasured.Text(panels) is { Length: > 0 } unmeasured ? $"{text} {unmeasured}" : text;

    // Route data is loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced
    // navigation between walls, and so is this component.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        message = null;
        failure = null;
        confirmRevert = false;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            status = await Placement.GetStatusAsync(WallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load the hold placement status of wall {WallId}", WallId);
            status = null;
        }
    }

    private Task PlaceAsync() => RunAsync(async () =>
    {
        var r = await Placement.PlaceAsync(WallId);
        message = WithUnmeasured($"{r.Placed} holds placed on the 3D model, {r.Skipped} left alone, {r.Failed} not measured.", r.Panels);
        if (r.Placed > 0)
        {
            message += " Their 3D shapes are refined in the background.";
        }
    });

    private Task RevertAsync(Guid runId) => RunAsync(async () =>
    {
        var result = await Placement.RevertAsync(WallId, runId);
        confirmRevert = false;
        message = $"Restored {result.Reverted} holds.";
        if (result.SkippedEdited.Count > 0)
        {
            message += $" {result.SkippedEdited.Count} were moved since and were left as they are.";
        }
    });

    /// <summary>Runs one action with the busy flag set, then reloads; failures become the status line.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        // A double tap lands before the disabled button reaches the phone: refuse the second run.
        if (busy)
        {
            return;
        }

        busy = true;
        message = null;
        failure = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Hold placement action failed for wall {WallId}", WallId);
            failure = ex is UserFacingException ? ex.Message : "That did not work. Please try again.";
        }
        finally
        {
            await ReloadAsync();
            busy = false;
        }
    }
}
