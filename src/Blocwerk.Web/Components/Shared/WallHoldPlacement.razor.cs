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
    private HoldOutlierPreview? outliers;
    private SurfaceRegistration? surfaceRegistration;
    private SurfacePreview? surface;

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

    /// <summary>The dry run of "re-place outlier holds in 3D" in one sentence.</summary>
    internal static string OutlierText(HoldOutlierPreview p) =>
        $"{p.ToReplace} of {p.Checked} placed holds disagree with the rest of the wall: {p.FromTwins} with their linked copy on a "
        + $"better photo, {p.FromNeighbours} with their neighbours. {p.Undecided} could not be judged and stay as they are.";

    /// <summary>The panel registration's state in a few words.</summary>
    internal static string SurfaceRegistrationText(SurfaceRegistration r) => r.State switch
    {
        "none" when !r.RunnerReady => "needs a 3D runner that can register panel photos (none is online for this wall).",
        "none" => "not run yet.",
        "queued" => "waiting for a 3D runner.",
        "running" => $"running ({r.Stage}).",
        "done" => $"{r.Registered} panel photo(s) registered.",
        _ => $"failed: {r.Error}",
    };

    /// <summary>The dry run of "place holds on the 3D surface" in one sentence.</summary>
    internal static string SurfaceText(SurfacePreview p) =>
        $"{p.ToPlace} of {p.Checked} holds would be placed on the surface: {p.OwnView} from their own photo, {p.SharedView} at a linked "
        + $"copy's position, {p.Fallback} by the outlier check; {p.Missed} stay as they are. Linked copies seen in both photos: "
        + $"{p.Pairs}, median {p.PairGapMedianMm?.ToString("0") ?? "-"} mm apart (p90 {p.PairGapP90Mm?.ToString("0") ?? "-"} mm).";

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
        outliers = null;
        surface = null;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            status = await Placement.GetStatusAsync(WallId);
            surfaceRegistration = await Placement.GetSurfaceRegistrationAsync(WallId);
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

    private Task CheckOutliersAsync() => RunAsync(async () =>
    {
        outliers = await Placement.PreviewOutliersAsync(WallId);
        message = OutlierText(outliers);
    });

    private Task ReplaceOutliersAsync() => RunAsync(async () =>
    {
        var r = await Placement.ReplaceOutliersAsync(WallId);
        outliers = null;
        message = $"{r.Placed} holds re-placed in 3D; the holds in the photos did not change. Their 3D shapes are refined in the background.";
    });

    private Task RegisterPanelsAsync() => RunAsync(async () =>
    {
        surfaceRegistration = await Placement.RegisterPanelsAsync(WallId);
        message = $"Panel photos: {SurfaceRegistrationText(surfaceRegistration)}";
    });

    private Task CheckSurfaceAsync() => RunAsync(async () =>
    {
        surface = await Placement.PreviewSurfaceAsync(WallId);
        message = SurfaceText(surface);
    });

    private Task PlaceOnSurfaceAsync() => RunAsync(async () =>
    {
        var r = await Placement.PlaceOnSurfaceAsync(WallId);
        surface = null;
        message = $"{r.Placed} holds placed on the 3D surface; the holds in the photos did not change. Their 3D shapes are refined in the background.";
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
