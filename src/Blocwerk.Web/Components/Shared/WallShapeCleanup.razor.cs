using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for the "Clean up hold shapes" wall-admin action: preview, apply and undo through
/// <see cref="IHoldShapeCleanupService"/> (one change-journal batch per apply). The geometry is CPU work, so it
/// runs on the thread pool (never on the circuit's dispatcher), reports progress, and can be cancelled.
/// </summary>
public partial class WallShapeCleanup : IDisposable
{
    private Guid loadedWallId;
    private bool busy;
    private string? message;
    private string? failure;
    private string? phase;
    private HoldShapeCleanupProgress progress;
    private CancellationTokenSource? cancellation;
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

    private string ProgressText => progress.Total == 0
        ? $"{phase}..."
        : $"{phase}... photo {Math.Min(progress.Done + 1, progress.Total)} of {progress.Total}";

    /// <summary>The dry-run sentence with the real categories.</summary>
    /// <param name="p">The preview counts.</param>
    /// <returns>Text for the admin.</returns>
    internal static string PreviewText(HoldShapeCleanupSummary p)
    {
        if (p.Changed == 0 && p.StillOverlapping == 0)
        {
            return $"All {p.AutoShapes + p.AutoCircles} automatic holds are already smooth and separate - nothing to do.";
        }

        var text = $"{p.Changed} of {p.AutoShapes + p.AutoCircles} automatic holds change: {p.Smoothed} smoothed, "
                   + $"{p.Clipped} trimmed to stop overlapping, {p.BackToCircle} back to a plain circle, {p.ShrunkCircle} circles shrunk.";
        return p.StillOverlapping > 0
            ? text + $" {p.StillOverlapping} still overlap something and are kept as they are."
            : text + " No overlaps remain between automatic holds.";
    }

    /// <summary>The hint about overlaps only a person can fix.</summary>
    /// <param name="p">The preview counts.</param>
    /// <returns>Text, or an empty string when there are none.</returns>
    internal static string ManualOverlapText(HoldShapeCleanupSummary p) => p.ManualOverlapCount == 0
        ? string.Empty
        : $"{p.ManualOverlapCount} overlap(s) involve only hand-placed or hand-drawn holds. Those are never changed automatically - "
          + "adjust them by hand in the hold editor.";

    /// <summary>A short label for a hold in the overlap list.</summary>
    /// <param name="name">The hold's name, if any.</param>
    /// <param name="id">The hold id.</param>
    /// <returns>The label.</returns>
    internal static string HoldLabel(string? name, Guid id) =>
        string.IsNullOrWhiteSpace(name) ? $"hold {id.ToString("N")[..6]}" : name;

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run just finished.
        }
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

    private void Cancel() => cancellation?.Cancel();

    private Task PreviewAsync() => RunAsync("Checking hold shapes", async (ct, report) =>
    {
        preview = await Cleanup.PreviewAsync(WallId, ct, report);
    });

    private Task ApplyAsync() => RunAsync("Cleaning up hold shapes", async (ct, report) =>
    {
        var result = await Cleanup.ApplyAsync(WallId, ct, preview?.PlanVersion, report);
        preview = null;
        message = result.BatchId is null ? "Nothing needed cleaning up." : $"Cleaned up {result.Changed} hold shapes.";
    });

    private Task UndoAsync(Guid batchId) => RunAsync("Undoing the clean-up", async (ct, _) =>
    {
        var result = await Cleanup.RevertAsync(WallId, batchId, ct);
        message = result.Reverted
            ? "Hold shapes restored."
            : "Some holds were edited since the clean-up, so nothing was undone.";
    });

    private async Task RunAsync(string label, Func<CancellationToken, IProgress<HoldShapeCleanupProgress>, Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        phase = label;
        progress = default;
        message = null;
        failure = null;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        var report = new Progress<HoldShapeCleanupProgress>(p =>
        {
            progress = p;
            _ = InvokeAsync(StateHasChanged);
        });
        try
        {
            // Off the circuit's dispatcher: the geometry would otherwise freeze every other click on the page.
            await Task.Run(() => action(token, report), token);
        }
        catch (OperationCanceledException)
        {
            message = "Cancelled - nothing was changed.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Shape clean-up action failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            await ReloadAsync();
            busy = false;
        }
    }
}
