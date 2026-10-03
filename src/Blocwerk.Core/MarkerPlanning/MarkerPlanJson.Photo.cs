// <copyright file="MarkerPlanJson.Photo.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Shape checks for the optional phone fields of <see cref="PhotoSetup"/>.</summary>
public static partial class MarkerPlanJson
{
    /// <summary>Longest phone model id accepted.</summary>
    public const int MaxPhoneModelLength = 64;

    /// <summary>
    /// The phone model, lens and closest distance are optional, but when present they must look like ids
    /// (a newer catalog's unknown model is fine — the validator warns, the stored FOV/px still size the
    /// markers), a lens needs a model, and the closest distance can't be past the farthest.
    /// </summary>
    private static void CheckPhoneFields(List<string> errors, PhotoSetup photo)
    {
        if (photo.PhoneModel is { } model && (model.Length > MaxPhoneModelLength || !PhoneIdPattern().IsMatch(model)))
        {
            errors.Add("photo.phoneModel must be a phone id such as \"iphone-16-pro\".");
        }

        if (photo.Lens is { } lens && !LensIdPattern().IsMatch(lens))
        {
            errors.Add("photo.lens must be a zoom such as \"0.5x\", \"1x\" or \"1.2x\".");
        }

        if (photo.Lens is not null && photo.PhoneModel is null)
        {
            errors.Add("photo.lens needs photo.phoneModel.");
        }

        if (photo.NearestDistanceMm is { } near)
        {
            Range(errors, "photo.nearestDistanceMm", near, MarkerPlanValidator.MinDistanceMm, MarkerPlanValidator.MaxDistanceMm);
            if (double.IsFinite(near) && near > photo.DistanceMm)
            {
                errors.Add("photo.nearestDistanceMm must not be larger than photo.distanceMm (the farthest usual distance).");
            }
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$")]
    private static partial Regex PhoneIdPattern();

    [GeneratedRegex("^[0-9]{1,2}(\\.[0-9]{1,2})?x$")]
    private static partial Regex LensIdPattern();
}
