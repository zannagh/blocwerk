// <copyright file="PhoneCameraCatalog.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The phones the marker planner knows, as data: per lens the saved photo's horizontal field of view and
/// long edge, which is all the sizing maths needs (<c>focal px = long edge / (2·tan(hfov/2))</c>). Each lens
/// row names its source. The <see cref="GenericPhoneId"/> entry reproduces the two legacy presets
/// (<c>phone-1x</c>, <c>phone-0.5x</c>) exactly, so plans saved before phone models existed size the same.
/// </summary>
public static partial class PhoneCameraCatalog
{
    /// <summary>The "any phone" entry that the legacy presets map to.</summary>
    public const string GenericPhoneId = "generic-phone";

    private static readonly Lazy<IReadOnlyList<PhoneCamera>> Phones = new(() => [.. Generic(), .. Apple(), .. Google(), .. Samsung()]);

    /// <summary>Every known phone: generic first, then by brand, oldest model first.</summary>
    public static IReadOnlyList<PhoneCamera> All => Phones.Value;

    /// <summary>The brands in display order.</summary>
    public static IReadOnlyList<string> Brands => All.Select(p => p.Brand).Distinct().ToList();

    /// <summary>The phone with <paramref name="id"/>, or null.</summary>
    public static PhoneCamera? Find(string? id) =>
        id is null ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The lens <paramref name="lensId"/> of phone <paramref name="phoneId"/>, or null.</summary>
    public static PhoneLens? FindLens(string? phoneId, string? lensId) => Find(phoneId)?.Lens(lensId);

    /// <summary>
    /// The phone and lens a photo setup describes: its own <see cref="PhotoSetup.PhoneModel"/> /
    /// <see cref="PhotoSetup.Lens"/> when set and known, else the generic phone lens its legacy
    /// <see cref="PhotoSetup.CameraPreset"/> names; null for "custom" or anything unknown.
    /// </summary>
    public static (PhoneCamera Phone, PhoneLens Lens)? Resolve(PhotoSetup photo)
    {
        if (photo.PhoneModel is not null)
        {
            var phone = Find(photo.PhoneModel);
            var lens = phone?.Lens(photo.Lens);
            return phone is not null && lens is not null ? (phone, lens) : null;
        }

        var legacy = photo.CameraPreset switch
        {
            MarkerCameraPresets.Phone1X => "1x",
            MarkerCameraPresets.PhoneUltraWide => "0.5x",
            _ => null,
        };
        var generic = Find(GenericPhoneId)!;
        return legacy is null ? null : (generic, generic.Lens(legacy)!);
    }

    private static IEnumerable<PhoneCamera> Generic() =>
    [
        new PhoneCamera(GenericPhoneId, "Generic", "Any phone (typical)",
        [
            new PhoneLens("0.5x", "Ultra-wide", Math.Round(PhoneLens.EquivalentFromFov(104)), 104, 4032, "Legacy preset phone-0.5x"),
            new PhoneLens("1x", "Main", Math.Round(PhoneLens.EquivalentFromFov(69)), 69, 4032, "Legacy preset phone-1x"),
        ]),
    ];
}
