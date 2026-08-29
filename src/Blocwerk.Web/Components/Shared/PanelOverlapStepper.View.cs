using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// View and input plumbing for <see cref="PanelOverlapStepper"/>: presentation helpers, the
/// keyboard-shortcut handler, and the JS key-trap lifecycle. The trap (see viewport.js)
/// suppresses the browser's default page scroll on the stepper's navigation keys while the
/// Blazor <c>@onkeydown</c> handler here still runs the step logic; it is attached on first
/// render and released on dispose.
/// </summary>
public partial class PanelOverlapStepper : IAsyncDisposable
{
    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private bool _keyTrapAttached;

    public async ValueTask DisposeAsync()
    {
        if (!_keyTrapAttached)
        {
            return;
        }

        try
        {
            await JS.InvokeVoidAsync("bwViewport.releaseStepperKeys", _rootRef);
        }
        catch (Exception)
        {
            // Circuit already gone during teardown — nothing to detach from.
        }
    }

    private async Task AttachKeyTrapAsync(bool firstRender)
    {
        if (!firstRender || _keyTrapAttached)
        {
            return;
        }

        _keyTrapAttached = true;
        try
        {
            await JS.InvokeVoidAsync("bwViewport.trapStepperKeys", _rootRef);
        }
        catch (Exception)
        {
            // The scroll-suppression trap is a nicety; a failed interop must never be fatal.
        }
    }

    private IReadOnlyList<PanelHold> NeighborHolds(Guid neighborId) =>
        _neighborHolds.GetValueOrDefault(neighborId) ?? [];

    private PanelHold? NeighborHold(Guid neighborId, Guid holdId) =>
        NeighborHolds(neighborId).FirstOrDefault(h => h.Id == holdId);

    private static (double X, double Y)? Point(PanelHold? h) => h is null ? null : (h.X, h.Y);

    private string PhotoUrl(Guid panelId, bool staged) =>
        $"/api/walls/{WallId}/panels/{panelId}/{(staged ? "staged-photo" : "photo")}";

    private int RemainingHighConfidence =>
        _steps.Skip(_index).Count(p => p.Confidence >= HighConfidence && !_removed.Contains(p.HoldAId));

    private static string PairColor(int i) => $"hsl({(i * 53) % 360}, 72%, 48%)";
    private static int Pct(double v) => (int)Math.Round(v * 100);
    private static string Band(double c) => c >= HighConfidence ? "high" : c >= 0.45 ? "mid" : "low";
    private static string BandLabel(double c) => c >= HighConfidence ? "very likely" : c >= 0.45 ? "likely — check" : "unlikely — check";

    private void OnKeyDown(KeyboardEventArgs e)
    {
        if (_loading || _finishing || _steps.Count == 0)
        {
            return;
        }

        if (_movedMode)
        {
            switch (e.Key)
            {
                case "Enter":
                case "c":
                case "C":
                    UseMovedHold();
                    break;
                case "Escape":
                    CancelMoved();
                    break;
            }

            return;
        }

        switch (e.Key)
        {
            case "Enter":
            case "c":
            case "C":
                ConfirmMatch();
                break;
            case "d":
            case "D":
                DiscardMatch();
                break;
            case "m":
            case "M":
                EnterMoved();
                break;
            case "x":
            case "X":
            case "Delete":
                DeleteHold();
                break;
            case "ArrowRight":
                Next();
                break;
            case "ArrowLeft":
                Back();
                break;
        }
    }
}
