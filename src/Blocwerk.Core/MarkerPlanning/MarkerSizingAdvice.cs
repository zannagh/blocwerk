// <copyright file="MarkerSizingAdvice.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Plain-language explanations of the marker sizes: why a surface's markers are the size they are (the
/// next smaller printable size and why it fails), the camera in words, and a close-up hint.
/// </summary>
public static class MarkerSizingAdvice
{
    /// <summary>The camera in words, e.g. "iPhone 16 Pro 0.5×" or "your camera (69°, 12 MP)".</summary>
    public static string DescribeCamera(PhotoSetup photo)
    {
        if (PhoneCameraCatalog.Resolve(photo) is { } camera)
        {
            var zoom = camera.Lens.Id.Replace('x', '×');
            return camera.Phone.Id == PhoneCameraCatalog.GenericPhoneId ? $"a phone at {zoom}" : $"{camera.Phone.Model} {zoom}";
        }

        var mp = photo.ImageLongEdgePx * (double)photo.ImageLongEdgePx * MarkerSizing.ShortSideRatio / 1e6;
        return F($"your camera ({photo.HorizontalFovDeg:0}°, {mp:0.#} MP)");
    }

    /// <summary>
    /// Why <paramref name="role"/> markers on <paramref name="segment"/> get the size the generator picks,
    /// e.g. "Corners: 60 mm (≈ 32 px). 50 mm would be ≈ 27 px from 3 m on iPhone 16 Pro 0.5× — under the 28 px a
    /// corner needs to decode reliably; 60 mm is the smallest that works" (the reason names pose or decoding).
    /// </summary>
    public static string Explain(MarkerRole role, PlanSegment segment, PhotoSetup photo, MarkerGenerationOptions options)
    {
        var what = role == MarkerRole.Corner ? "corner" : "filler";
        var need = MarkerSizing.RequiredPx(role, segment, photo, options);
        var why = Reason(role, segment, photo, options);
        var size = MarkerSizing.PickSize(role, segment, photo, options, out var works);
        var px = MarkerSizing.EstimatedPx(size, segment, photo);
        var from = F($"from {photo.DistanceMm / 1000:0.0#} m on {DescribeCamera(photo)}");
        var steep = !MarkerSizing.PoseBinds(role, segment, photo, options)
                    && MarkerDetectability.SteepViewFactor(MarkerSizing.IsGrazing(segment) ? 0 : MarkerSizing.ObliquenessDeg(segment)) > 1
            ? F($" (incl. a steep-view margin: the surface is turned {MarkerSizing.ObliquenessDeg(segment):0}° away)")
            : string.Empty;
        var head = F($"{Capital(what)}s: {size:0} mm (≈ {px:0} px {from}).");
        if (!works)
        {
            var needMm = Math.Ceiling(MarkerSizing.RequiredSizeMm(need, segment, photo) / 5) * 5;
            return F($"{head} Even that is under the {need:0} px a {what} needs {why}{steep} — it would take {needMm:0} mm; photograph this surface from closer or with a narrower lens.");
        }

        var smaller = options.AvailableSizesMm.Where(s => s < size && s > 0).DefaultIfEmpty(0).Max();
        if (smaller <= 0)
        {
            return F($"{head} The smallest printable size already reaches the {need:0} px a {what} needs {why}{steep}.");
        }

        var smallerPx = MarkerSizing.EstimatedPx(smaller, segment, photo);
        return F($"{head} {smaller:0} mm would be ≈ {smallerPx:0} px — under the {need:0} px a {what} needs {why}{steep}; {size:0} mm is the smallest that works.");
    }

    /// <summary>What sets the px target: "to decode reliably" or "for ~5 mm position accuracy from 3 m".</summary>
    public static string Reason(MarkerRole role, PlanSegment segment, PhotoSetup photo, MarkerGenerationOptions options) =>
        MarkerSizing.PoseBinds(role, segment, photo, options)
            ? F($"for ~{(role == MarkerRole.Corner ? 5 : 8)} mm position accuracy from {photo.DistanceMm / 1000:0.0#} m")
            : "to decode reliably";

    /// <summary>
    /// A hint for the owner's close-ups (<see cref="PhotoSetup.NearestDistanceMm"/>): markers are spaced for
    /// the far shots, so a close-up may see only one or two. Null when no closest distance is set.
    /// </summary>
    public static string? CloseUpHint(PhotoSetup photo)
    {
        if (photo.NearestDistanceMm is not { } near || !double.IsFinite(near) || near <= 0)
        {
            return null;
        }

        var close = photo with { DistanceMm = near };
        var height = MarkerSizing.FootprintHeightMm(close);
        var spacing = MarkerSizing.MaxSpacingMm(photo);
        var across = height / spacing;
        var text = F($"Close-ups from {near / 1000:0.0#} m cover about {MarkerSizing.FootprintWidthMm(close) / 1000:0.0#} × {height / 1000:0.0#} m and make every marker {photo.DistanceMm / near:0.#}× bigger than planned.");
        return across < 2
            ? text + F($" The markers are up to {spacing / 1000:0.0#} m apart, so a close-up may see only one — keep the wide shots from {photo.DistanceMm / 1000:0.0#} m to tie them together.")
            : text + " They still see several markers each.";
    }

    private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
