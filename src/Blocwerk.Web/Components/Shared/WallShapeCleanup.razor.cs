using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for the "Clean up hold shapes" wall-admin action: preview, apply and undo through
/// <see cref="IHoldShapeCleanupService"/> (one change-journal batch per apply).
/// </summary>
public partial class WallShapeCleanup
{
    private Guid loadedWallId;
    private bool busy;
    private string? message;
    private string? failure;
    private HoldShapeCleanupStatus? status;
    private HoldShapeCleanupSummary? preview;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: the action is never offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IHoldShapeCleanupService Cleanup { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallShapeCleanup> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>The dry-run sentence.</summary>
    /// <param name="p">The preview counts.</param>
    /// <returns>Text for the admin.</returns>
    internal static string PreviewText(HoldShapeCleanupSummary p)
    {
        var changes = p.Smoothed + p.Clipped + p.BackToCircle;
        if (changes == 0)
        {
            return $"All {p.AutoShapes} automatic shapes are already smooth and separate - nothing to do.";
        }

        return $"{changes} of {p.AutoShapes} automatic shapes change: {p.Smoothed} smoothed, {p.Clipped} trimmed to stop overlapping, "
               + $"{p.BackToCircle} back to a circle. {p.LockedHolds} other holds are left alone.";
    }

    // Route data is loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced navigation.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        preview = null;
        message = null;
        failure = null;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            status = await Cleanup.GetStatusAsync(WallId);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load the shape clean-up status of wall {WallId}", WallId);
            status = null;
        }
    }

    private void CancelPreview() => preview = null;

    private Task PreviewAsync() => RunAsync(async () => preview = await Cleanup.PreviewAsync(WallId));

    private Task ApplyAsync() => RunAsync(async () =>
    {
        var result = await Cleanup.ApplyAsync(WallId);
        preview = null;
        message = $"Cleaned up {result.Smoothed + result.Clipped + result.BackToCircle} hold shapes.";
    });

    private Task UndoAsync(Guid batchId) => RunAsync(async () =>
    {
        var result = await Cleanup.RevertAsync(WallId, batchId);
        message = result.Reverted
            ? "Hold shapes restored."
            : "Some holds were edited since the clean-up, so nothing was undone.";
    });

    private async Task RunAsync(Func<Task> action)
    {
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
            Logger.LogWarning(ex, "Shape clean-up action failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            await ReloadAsync();
            busy = false;
        }
    }
}
