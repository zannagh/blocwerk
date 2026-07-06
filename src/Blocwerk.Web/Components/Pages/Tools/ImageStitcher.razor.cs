using System.Globalization;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Tools;

public partial class ImageStitcher
{
    private sealed class StitcherLayer
    {
        public Guid Id { get; init; } = Guid.NewGuid();

        public string DataUrl { get; init; } = "";

        public double Width { get; init; }

        public double Height { get; init; }

        public (double X, double Y)[] Corners { get; set; } = new (double, double)[4];

        public double Opacity { get; set; } = 1.0;
    }

    private enum DragKind { None, Pan, Move, Corner }

    private readonly List<StitcherLayer> _layers = [];
    private Guid? _selectedId;
    private ElementReference _viewportRef;
    private List<Wall>? _myWalls;
    private Guid _targetWallId;
    private string? _toast;
    private bool _exporting;
    private bool _aligning;

    // Viewport transform: world -> screen is (pan + world * zoom).
    private double _zoom = 1.0;
    private double _panX;
    private double _panY;

    private DragKind _dragKind = DragKind.None;
    private int _dragCorner;
    private double _panStartClientX, _panStartClientY, _panOrigX, _panOrigY;
    private double _dragStartWorldX, _dragStartWorldY;
    private (double X, double Y)[] _dragStartCorners = [];

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var me = await CurrentUserService.GetCurrentUserAsync();
            var walls = await WallService.GetMyWallsAsync();
            _myWalls = walls
                .Where(w => w.Members.Any(m => m.UserId == me.Id && m.Role == WallRole.Admin))
                .OrderBy(w => w.Name)
                .ToList();
        }
        catch
        {
            _myWalls = [];
        }
    }

    private async Task OnUpload(InputFileChangeEventArgs e)
    {
        foreach (var file in e.GetMultipleFiles(20))
        {
            if (file.Size > 20 * 1024 * 1024)
            {
                await ShowToast($"{file.Name} exceeds 20 MB — skipped");
                continue;
            }

            try
            {
                using var stream = file.OpenReadStream(20 * 1024 * 1024);
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var dataUrl = $"data:{file.ContentType};base64,{Convert.ToBase64String(ms.ToArray())}";
                var probed = await JS.InvokeAsync<ProbedImage>("imageStitcher.probeDimensions", dataUrl);
                var offset = _layers.Count * 60.0;
                _layers.Add(new StitcherLayer
                {
                    DataUrl = dataUrl,
                    Width = probed.Width,
                    Height = probed.Height,
                    Corners = RectCorners(offset, offset, probed.Width, probed.Height),
                });
            }
            catch (Exception ex)
            {
                await ShowToast($"Failed to add {file.Name}: {ex.Message}");
            }
        }

        _selectedId = _layers.LastOrDefault()?.Id;
        await FitToContent();
    }

    private record ProbedImage(double Width, double Height);

    private static (double X, double Y)[] RectCorners(double x, double y, double w, double h) =>
        [(x, y), (x + w, y), (x + w, y + h), (x, y + h)];

    private void ResetCorners(StitcherLayer layer)
    {
        var minX = layer.Corners.Min(c => c.X);
        var minY = layer.Corners.Min(c => c.Y);
        layer.Corners = RectCorners(minX, minY, layer.Width, layer.Height);
    }

    // ---- viewport / pan / zoom --------------------------------------------
    private (double X, double Y) ToScreen((double X, double Y) world) =>
        (_panX + (world.X * _zoom), _panY + (world.Y * _zoom));

    private async Task<(double X, double Y)> ToWorld(double clientX, double clientY)
    {
        var p = await JS.InvokeAsync<Point>(
            "imageStitcher.clientToWorld", _viewportRef, clientX, clientY, _panX, _panY, _zoom);
        return (p.X, p.Y);
    }

    private record Point(double X, double Y);

    private Task ZoomIn() => ZoomAboutCenter(1.25);

    private Task ZoomOut() => ZoomAboutCenter(1 / 1.25);

    private async Task ZoomAboutCenter(double factor)
    {
        var vp = await JS.InvokeAsync<ViewportRect>("imageStitcher.getViewportSize", _viewportRef);
        ZoomAbout(vp.Width / 2, vp.Height / 2, factor);
        StateHasChanged();
    }

    private void ZoomAbout(double screenX, double screenY, double factor)
    {
        var newZoom = Math.Clamp(_zoom * factor, 0.02, 20.0);
        var worldX = (screenX - _panX) / _zoom;
        var worldY = (screenY - _panY) / _zoom;
        _zoom = newZoom;
        _panX = screenX - (worldX * _zoom);
        _panY = screenY - (worldY * _zoom);
    }

    private async Task OnWheel(WheelEventArgs e)
    {
        // Anchor the zoom on the cursor's viewport-relative position (not OffsetX,
        // which is relative to whatever child element the pointer is over).
        var vp = await JS.InvokeAsync<ViewportRect>("imageStitcher.getViewportSize", _viewportRef);
        var factor = e.DeltaY < 0 ? 1.1 : 1 / 1.1;
        ZoomAbout(e.ClientX - vp.Left, e.ClientY - vp.Top, factor);
        StateHasChanged();
    }

    private async Task FitToContent()
    {
        if (_layers.Count == 0)
        {
            return;
        }

        var vp = await JS.InvokeAsync<ViewportRect>("imageStitcher.getViewportSize", _viewportRef);
        if (vp.Width <= 0 || vp.Height <= 0)
        {
            return;
        }

        var all = _layers.SelectMany(l => l.Corners).ToArray();
        var minX = all.Min(c => c.X);
        var minY = all.Min(c => c.Y);
        var maxX = all.Max(c => c.X);
        var maxY = all.Max(c => c.Y);
        var bw = Math.Max(1, maxX - minX);
        var bh = Math.Max(1, maxY - minY);

        _zoom = Math.Clamp(Math.Min(vp.Width / bw, vp.Height / bh) * 0.9, 0.02, 20.0);
        _panX = (vp.Width / 2) - ((minX + maxX) / 2 * _zoom);
        _panY = (vp.Height / 2) - ((minY + maxY) / 2 * _zoom);
        StateHasChanged();
    }

    private record ViewportRect(double Width, double Height, double Left, double Top);

    // ---- dragging ----------------------------------------------------------
    private void StartPan(MouseEventArgs e)
    {
        _dragKind = DragKind.Pan;
        _panStartClientX = e.ClientX;
        _panStartClientY = e.ClientY;
        _panOrigX = _panX;
        _panOrigY = _panY;
    }

    private async Task StartLayerMove(MouseEventArgs e, StitcherLayer layer)
    {
        _selectedId = layer.Id;
        _dragKind = DragKind.Move;
        _dragStartCorners = (( double X, double Y)[])layer.Corners.Clone();
        var w = await ToWorld(e.ClientX, e.ClientY);
        _dragStartWorldX = w.X;
        _dragStartWorldY = w.Y;
    }

    private async Task StartCorner(MouseEventArgs e, int idx)
    {
        var sel = _layers.FirstOrDefault(l => l.Id == _selectedId);
        if (sel == null)
        {
            return;
        }

        _dragKind = DragKind.Corner;
        _dragCorner = idx;
        _dragStartCorners = ((double X, double Y)[])sel.Corners.Clone();
        var w = await ToWorld(e.ClientX, e.ClientY);
        _dragStartWorldX = w.X;
        _dragStartWorldY = w.Y;
    }

    private async Task OnPointerMove(MouseEventArgs e)
    {
        if (_dragKind == DragKind.Pan)
        {
            _panX = _panOrigX + (e.ClientX - _panStartClientX);
            _panY = _panOrigY + (e.ClientY - _panStartClientY);
            StateHasChanged();
            return;
        }

        if (_dragKind is not (DragKind.Move or DragKind.Corner))
        {
            return;
        }

        var sel = _layers.FirstOrDefault(l => l.Id == _selectedId);
        if (sel == null)
        {
            return;
        }

        var w = await ToWorld(e.ClientX, e.ClientY);
        if (_dragKind == DragKind.None)
        {
            return; // released during the interop hop
        }

        var dx = w.X - _dragStartWorldX;
        var dy = w.Y - _dragStartWorldY;

        if (_dragKind == DragKind.Move)
        {
            for (var i = 0; i < 4; i++)
            {
                sel.Corners[i] = (_dragStartCorners[i].X + dx, _dragStartCorners[i].Y + dy);
            }
        }
        else
        {
            sel.Corners[_dragCorner] = (_dragStartCorners[_dragCorner].X + dx, _dragStartCorners[_dragCorner].Y + dy);
        }

        StateHasChanged();
    }

    private void OnPointerUp() => _dragKind = DragKind.None;

    // ---- layer ops ---------------------------------------------------------
    private void BringForward()
    {
        var idx = _layers.FindIndex(l => l.Id == _selectedId);
        if (idx < 0 || idx == _layers.Count - 1)
        {
            return;
        }

        (_layers[idx], _layers[idx + 1]) = (_layers[idx + 1], _layers[idx]);
    }

    private void SendBackward()
    {
        var idx = _layers.FindIndex(l => l.Id == _selectedId);
        if (idx <= 0)
        {
            return;
        }

        (_layers[idx], _layers[idx - 1]) = (_layers[idx - 1], _layers[idx]);
    }

    private void DeleteSelected()
    {
        _layers.RemoveAll(l => l.Id == _selectedId);
        _selectedId = _layers.LastOrDefault()?.Id;
    }

    private void OnOpacityChanged(ChangeEventArgs e)
    {
        var sel = _layers.FirstOrDefault(l => l.Id == _selectedId);
        if (sel != null && int.TryParse(e.Value?.ToString(), out var pct))
        {
            sel.Opacity = Math.Clamp(pct / 100.0, 0.05, 1.0);
        }
    }

    private void ResetOpacity()
    {
        var sel = _layers.FirstOrDefault(l => l.Id == _selectedId);
        if (sel != null)
        {
            sel.Opacity = 1.0;
        }
    }

    // ---- auto-align (server-side homography) -------------------------------
    private async Task AutoAlign()
    {
        var idx = _layers.FindIndex(l => l.Id == _selectedId);
        if (idx <= 0)
        {
            await ShowToast("Put this image above another to auto-align it");
            return;
        }

        _aligning = true;
        StateHasChanged();
        try
        {
            var target = _layers[idx];
            var baseLayer = _layers[idx - 1];
            var h = await ImageAlignment.AlignAsync(DecodeDataUrl(baseLayer.DataUrl), DecodeDataUrl(target.DataUrl));
            if (h == null)
            {
                await ShowToast("Couldn't find a match — align by hand");
                return;
            }

            // target source corners -> base pixel frame (H) -> world (base src->world).
            var baseToWorld = StitcherMath.ProjectionForCorners(baseLayer.Width, baseLayer.Height, baseLayer.Corners);
            var src = RectCorners(0, 0, target.Width, target.Height);
            var world = new (double X, double Y)[4];
            for (var i = 0; i < 4; i++)
            {
                var (bx, by) = h.Project(src[i].X, src[i].Y);
                world[i] = StitcherMath.Apply(baseToWorld, bx, by);
            }

            target.Corners = world;
            await ShowToast($"Aligned (confidence {h.Confidence:0.00})");
        }
        catch (Exception ex)
        {
            await ShowToast($"Auto-align failed: {ex.Message}");
        }
        finally
        {
            _aligning = false;
            StateHasChanged();
        }
    }

    private static byte[] DecodeDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        return Convert.FromBase64String(comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl);
    }

    // ---- export ------------------------------------------------------------
    private object LayerForJs(StitcherLayer l) => new
    {
        dataUrl = l.DataUrl,
        width = l.Width,
        height = l.Height,
        corners = l.Corners.Select(c => new[] { c.X, c.Y }).ToArray(),
    };

    private async Task ExportDownload()
    {
        if (_layers.Count == 0)
        {
            await ShowToast("Nothing to export");
            return;
        }

        _exporting = true;
        StateHasChanged();
        try
        {
            var jsLayers = _layers.Select(LayerForJs).ToArray();
            var filename = $"blocwerk-stitched-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.png";
            var ok = await JS.InvokeAsync<bool>("imageStitcher.downloadPng", (object)jsLayers, filename);
            if (!ok)
            {
                await ShowToast("Export failed");
            }
        }
        catch (Exception ex)
        {
            await ShowToast($"Export failed: {ex.Message}");
        }
        finally
        {
            _exporting = false;
            StateHasChanged();
        }
    }

    private async Task ExportToWall()
    {
        if (_targetWallId == Guid.Empty || _layers.Count == 0)
        {
            return;
        }

        _exporting = true;
        StateHasChanged();
        try
        {
            var jsLayers = _layers.Select(LayerForJs).ToArray();
            var streamRef = await JS.InvokeAsync<IJSStreamReference>("imageStitcher.exportPngBlob", (object)jsLayers);
            if (streamRef == null)
            {
                await ShowToast("Nothing to export");
                return;
            }

            await using var stream = await streamRef.OpenReadStreamAsync(maxAllowedSize: 64 * 1024 * 1024);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);
            var bytes = ms.ToArray();

            var wall = _myWalls?.FirstOrDefault(w => w.Id == _targetWallId);
            if (wall == null)
            {
                await ShowToast("Wall not found");
                return;
            }

            if (wall.PhotoContentType != null)
            {
                await WallService.StagePhotoAsync(_targetWallId, bytes, "image/png");
                await ShowToast("Photo staged — go to the wall to align holds");
            }
            else
            {
                await WallService.UploadPhotoAsync(_targetWallId, bytes, "image/png");
                await ShowToast("Photo uploaded");
            }

            Navigation.NavigateTo($"/walls/{_targetWallId}");
        }
        catch (Exception ex)
        {
            await ShowToast($"Stage failed: {ex.Message}");
        }
        finally
        {
            _exporting = false;
            StateHasChanged();
        }
    }

    private async Task ShowToast(string message)
    {
        _toast = message;
        StateHasChanged();
        await Task.Delay(2500);
        _toast = null;
        StateHasChanged();
    }

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Px(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + "px";
}
