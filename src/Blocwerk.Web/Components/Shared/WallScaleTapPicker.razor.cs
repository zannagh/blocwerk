// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Capture;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>Code-behind of the two-tap distance picker: taps become stored-photo pixels (the JS reports fractions).</summary>
public partial class WallScaleTapPicker : IAsyncDisposable
{
    private readonly string idSuffix = Guid.NewGuid().ToString("N")[..8];
    private ElementReference imageBox;
    private IJSObjectReference? module;
    private CapturePhotoResult? photo;
    private double[]? a;
    private double[]? b;
    private double? mm;
    private bool zoomed;
    private CaptureScaleReference? appliedInitial;

    /// <summary>The capture whose photos are shown.</summary>
    [Parameter]
    [EditorRequired]
    public Guid CaptureId { get; set; }

    /// <summary>The photos to choose from.</summary>
    [Parameter]
    [EditorRequired]
    public IReadOnlyList<CapturePhotoResult> Photos { get; set; } = [];

    /// <summary>A distance already given (shown for editing), or null.</summary>
    [Parameter]
    public CaptureScaleReference? Initial { get; set; }

    [Parameter]
    public string SaveLabel { get; set; } = "Use this distance";

    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Offer "Don't use a distance" (a draft's saved one).</summary>
    [Parameter]
    public bool ShowClear { get; set; }

    [Parameter]
    public EventCallback<CaptureScaleReference> OnSave { get; set; }

    [Parameter]
    public EventCallback OnClear { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    private IEnumerable<double[]> Points => new[] { a, b }.OfType<double[]>();

    private bool Ready => photo is not null && a is not null && b is not null && mm is > 0;

    private double Stroke => photo is null ? 4 : Math.Max(photo.Width, photo.Height) / 400.0;

    private string Status => (a, b) switch
    {
        (null, _) => "Tap the first point.",
        (_, null) => "Tap the second point.",
        _ => string.Create(CultureInfo.InvariantCulture, $"Two points {PixelDistance:0} px apart on the photo. Enter their distance below."),
    };

    private double PixelDistance => a is null || b is null ? 0 : Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2));

    public async ValueTask DisposeAsync()
    {
        if (module is not null)
        {
            try
            {
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The circuit is gone.
            }
        }

        GC.SuppressFinalize(this);
    }

    protected override void OnParametersSet()
    {
        if (Initial is { } initial && !ReferenceEquals(initial, appliedInitial))
        {
            appliedInitial = initial;
            photo = Photos.FirstOrDefault(p => p.Index == initial.PhotoIndex);
            (a, b, mm) = photo is null ? (null, null, null) : (initial.A, initial.B, (double?)initial.Mm);
        }
    }

    private static string Name(CapturePhotoResult p) => p.FileName ?? $"Photo {p.Index}";

    private static string Px(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    private void ChoosePhoto(ChangeEventArgs e)
    {
        photo = Guid.TryParse(e.Value?.ToString(), out var id) ? Photos.FirstOrDefault(p => p.PhotoId == id) : null;
        (a, b) = (null, null);
    }

    private async Task TapAsync(MouseEventArgs e)
    {
        if (Disabled || photo is null)
        {
            return;
        }

        module ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/scale-tap.js");
        var f = await module.InvokeAsync<double[]?>("fraction", imageBox, e.ClientX, e.ClientY);
        if (f is not { Length: 2 })
        {
            return;
        }

        double[] point = [Math.Round(f[0] * photo.Width, 1), Math.Round(f[1] * photo.Height, 1)];
        if (a is null || b is not null)
        {
            (a, b) = (point, null);
        }
        else
        {
            b = point;
        }
    }

    private Task SaveAsync() =>
        Ready ? OnSave.InvokeAsync(new CaptureScaleReference(photo!.Index, a!, b!, mm!.Value)) : Task.CompletedTask;
}
