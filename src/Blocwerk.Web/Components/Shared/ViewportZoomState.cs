using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Display-only mirror of the zoom level owned by the JS viewport engine
/// (wwwroot/js/viewport.js).
/// </summary>
/// <remarks>
/// JS is the sole authority for the viewport transform and applies it synchronously;
/// this type only receives a trailing-debounced notification so the UI can show a
/// zoom read-out or scale SVG handles. Nothing here may feed back into the geometry,
/// otherwise a late server render would stomp what JS already applied.
/// </remarks>
public sealed class ViewportZoomState : IDisposable
{
    private readonly Func<Task>? onChanged;
    private DotNetObjectReference<ViewportZoomState>? selfRef;

    /// <summary>
    /// Creates the state. <paramref name="onChanged"/> is normally
    /// <c>() =&gt; InvokeAsync(StateHasChanged)</c> so the update is marshalled onto
    /// the renderer's synchronisation context.
    /// </summary>
    public ViewportZoomState(Func<Task>? onChanged = null)
    {
        this.onChanged = onChanged;
    }

    /// <summary>Gets the last zoom level reported by JS. 1.0 means "fit".</summary>
    public double Zoom { get; private set; } = 1.0;

    /// <summary>Gets the reference handed to <c>bwViewport.setupScroll</c>.</summary>
    public DotNetObjectReference<ViewportZoomState> Reference =>
        selfRef ??= DotNetObjectReference.Create(this);

    [JSInvokable]
    public async Task SetZoomFromJs(double zoom)
    {
        // Re-entrancy guard: identical values must not trigger another render pass.
        if (Math.Abs(zoom - Zoom) < 0.005)
        {
            return;
        }

        Zoom = zoom;
        if (onChanged != null)
        {
            await onChanged();
        }
    }

    public void Dispose()
    {
        selfRef?.Dispose();
        selfRef = null;
    }
}
