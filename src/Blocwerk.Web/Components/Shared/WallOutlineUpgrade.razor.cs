using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for the "detect real outlines for existing holds" wall-admin action: loads whether it is
/// available and the latest run, and routes preview / apply / revert through <see cref="IHoldOutlineUpgradeService"/>.
/// </summary>
public partial class WallOutlineUpgrade
{
    private Guid loadedWallId;
    private bool loading = true;
    private bool busy;
    private bool includeManual;
    private bool confirmRevert;
    private string? message;
    private string? failure;
    private HoldOutlineUpgradeStatus? status;
    private HoldOutlineUpgradePreview? preview;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: the action is never offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IHoldOutlineUpgradeService Upgrades { get; set; } = default!;

    [Inject]
    private IHoldFootprintService Footprints { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallOutlineUpgrade> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>The dry-run sentence, e.g. "412 of 629 circle holds get an outline; 38 have a pocket hole; 179 stay circles".</summary>
    internal static string PreviewText(HoldOutlineUpgradePreview p)
    {
        if (p.Eligible == 0)
        {
            return "Every hold already has an outline — nothing to do.";
        }

        var text = $"{p.WouldOutline} of {p.Eligible} circle holds get an outline";
        if (p.WithHoles > 0)
        {
            text += $"; {p.WithHoles} {(p.WithHoles == 1 ? "has a pocket hole" : "have a pocket hole")}";
        }

        return text + $"; {p.WouldKeepCircle} stay {(p.WouldKeepCircle == 1 ? "a circle" : "circles")}.";
    }

    // Route data is loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced
    // navigation between walls, and so is this component.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        loading = true;
        preview = null;
        message = null;
        failure = null;
        await ReloadAsync();
        loading = false;
    }

    private async Task ReloadAsync()
    {
        try
        {
            status = await Upgrades.GetStatusAsync(WallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load the outline upgrade status of wall {WallId}", WallId);
            status = null;
        }
    }

    private void OnIncludeManualChanged(ChangeEventArgs e)
    {
        includeManual = e.Value is true;
        preview = null;
    }

    private void CancelPreview() => preview = null;

    private Task PreviewAsync() => RunAsync(async () =>
    {
        preview = await Upgrades.PreviewAsync(WallId, new HoldOutlineUpgradeOptions(includeManual));
    });

    private Task ApplyAsync() => RunAsync(async () =>
    {
        var result = await Upgrades.ApplyAsync(WallId, new HoldOutlineUpgradeOptions(includeManual));
        preview = null;
        message = $"{result.Outlined} holds now have their real outline; {result.KeptCircle} stayed circles.";
        if (result.SkippedChanged > 0)
        {
            message += $" {result.SkippedChanged} were edited meanwhile and left alone.";
        }
    });

    private Task RefineFootprintsAsync() => RunAsync(async () =>
    {
        var r = await Footprints.RefineAsync(WallId);
        message = r.CapturePhotos == 0
            ? $"No 3D capture photos: {r.SingleView} holds got an approximate correction from their wall photo."
            : $"{r.MultiView} holds refined from several views, {r.SingleView} approximated from one view.";
    });

    private Task RevertAsync(Guid runId) => RunAsync(async () =>
    {
        var result = await Upgrades.RevertAsync(WallId, runId);
        confirmRevert = false;
        message = $"Restored {result.Reverted} holds.";
        if (result.SkippedEdited.Count > 0)
        {
            message += $" {result.SkippedEdited.Count} were edited since the upgrade and were left as they are.";
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
            Logger.LogWarning(ex, "Outline upgrade action failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            await ReloadAsync();
            busy = false;
        }
    }
}
