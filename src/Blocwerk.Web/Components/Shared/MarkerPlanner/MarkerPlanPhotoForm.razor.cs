// <copyright file="MarkerPlanPhotoForm.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.MarkerPlanner;

/// <summary>Photo setup form: phone + lens (or a custom camera), farthest and closest usual distance.</summary>
public partial class MarkerPlanPhotoForm
{
    private string distanceText = string.Empty;
    private double shownDistance = double.NaN;
    private string? distanceError;
    private string nearText = string.Empty;
    private double? shownNear;
    private string? nearError;

    /// <summary>The current photo setup.</summary>
    [Parameter]
    [EditorRequired]
    public PhotoSetup Photo { get; set; } = default!;

    /// <summary>The photo setup changed.</summary>
    [Parameter]
    public EventCallback<PhotoSetup> OnChange { get; set; }

    private (PhoneCamera Phone, PhoneLens Lens)? Resolved => PhoneCameraCatalog.Resolve(Photo);

    private string PhoneValue => Resolved?.Phone.Id ?? MarkerCameraPresets.Custom;

    private double Megapixels => Photo.ImageLongEdgePx * (double)Photo.ImageLongEdgePx * MarkerSizing.ShortSideRatio / 1e6;

    /// <inheritdoc/>
    protected override void OnParametersSet()
    {
        // Only reformat when a distance changed from outside (a new plan, an import) — never while the
        // owner's own text is the source of the value.
        if (!shownDistance.Equals(Photo.DistanceMm))
        {
            shownDistance = Photo.DistanceMm;
            distanceText = PhotoDistanceParser.Format(Photo.DistanceMm);
            distanceError = null;
        }

        if (shownNear != Photo.NearestDistanceMm)
        {
            shownNear = Photo.NearestDistanceMm;
            nearText = Photo.NearestDistanceMm is { } near ? PhotoDistanceParser.Format(near) : string.Empty;
            nearError = null;
        }
    }

    private async Task OnDistanceChanged(ChangeEventArgs e)
    {
        distanceText = e.Value?.ToString() ?? string.Empty;
        if (PhotoDistanceParser.ParseMm(distanceText) is not { } mm)
        {
            distanceError = "Type a distance such as 3000, 3 m or 300 cm.";
            return;
        }

        distanceError = null;
        shownDistance = mm;
        var near = Photo.NearestDistanceMm is { } n && n > mm ? null : Photo.NearestDistanceMm;
        await OnChange.InvokeAsync(Photo with { DistanceMm = mm, NearestDistanceMm = near });
    }

    private async Task OnNearChanged(ChangeEventArgs e)
    {
        nearText = e.Value?.ToString() ?? string.Empty;
        double? near = null;
        if (!string.IsNullOrWhiteSpace(nearText))
        {
            if (PhotoDistanceParser.ParseMm(nearText) is not { } mm)
            {
                nearError = "Type a distance such as 1200, 1.2 m or 120 cm — or leave it empty.";
                return;
            }

            if (mm > Photo.DistanceMm)
            {
                nearError = "The closest distance can't be farther than the farthest one.";
                return;
            }

            near = mm;
        }

        nearError = null;
        shownNear = near;
        await OnChange.InvokeAsync(Photo with { NearestDistanceMm = near });
    }

    private Task OnPhoneChanged(ChangeEventArgs e)
    {
        var phone = PhoneCameraCatalog.Find(e.Value?.ToString());
        if (phone is null)
        {
            return OnChange.InvokeAsync(Photo with { CameraPreset = MarkerCameraPresets.Custom, PhoneModel = null, Lens = null });
        }

        // Keep the zoom the owner had (0.5× stays 0.5×) when the new phone has it.
        var lens = phone.Lens(Resolved?.Lens.Id) ?? phone.MainLens;
        return OnChange.InvokeAsync(MarkerCameraPresets.ForPhone(phone, lens, Photo.DistanceMm, Photo.NearestDistanceMm));
    }

    private Task OnLensChanged(ChangeEventArgs e) =>
        Resolved is { } camera && camera.Phone.Lens(e.Value?.ToString()) is { } lens
            ? OnChange.InvokeAsync(MarkerCameraPresets.ForPhone(camera.Phone, lens, Photo.DistanceMm, Photo.NearestDistanceMm))
            : Task.CompletedTask;

    private PhotoSetup? FromMegapixels(double mp)
    {
        var px = MarkerCameraPresets.LongEdgeFromMegapixels(mp);
        return px is >= 640 and <= 20000 ? Photo with { ImageLongEdgePx = px } : null;
    }

    private Task ApplyCustom(ChangeEventArgs e, Func<double, PhotoSetup?> update) =>
        PlannerInput.Number(e.Value) is { } v && update(v) is { } updated ? OnChange.InvokeAsync(updated) : Task.CompletedTask;
}
