// <copyright file="MarkerPlanValidator.Camera.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Checks of the phone model / lens against <see cref="PhoneCameraCatalog"/>.</summary>
public static partial class MarkerPlanValidator
{
    /// <summary>How far (degrees) the stored FOV may drift from the catalog's lens before we say so.</summary>
    private const double FovToleranceDeg = 0.5;

    private static void CheckCamera(PhotoSetup photo, List<PlanIssue> issues)
    {
        if (photo.PhoneModel is null)
        {
            return;
        }

        var phone = PhoneCameraCatalog.Find(photo.PhoneModel);
        var lens = phone?.Lens(photo.Lens);
        if (phone is null || lens is null)
        {
            var what = phone is null ? $"phone \"{photo.PhoneModel}\"" : $"lens \"{photo.Lens}\" of the {phone.Model}";
            issues.Add(Warning("photo-camera-unknown", $"This Blocwerk doesn't know the {what}; the markers are sized from the stored {photo.HorizontalFovDeg:0}° / {photo.ImageLongEdgePx} px. Pick your phone again if that looks wrong.", null, null));
            return;
        }

        if (Math.Abs(lens.HorizontalFovDeg - photo.HorizontalFovDeg) > FovToleranceDeg || lens.ImageLongEdgePx != photo.ImageLongEdgePx)
        {
            issues.Add(Warning("photo-camera-mismatch", $"The plan says {phone.Model} {lens.Id.Replace('x', '×')} ({lens.HorizontalFovDeg:0}°, {lens.ImageLongEdgePx} px) but stores {photo.HorizontalFovDeg:0}° / {photo.ImageLongEdgePx} px; the stored values size the markers. Pick the lens again to use the phone's.", null, null));
        }
    }
}
