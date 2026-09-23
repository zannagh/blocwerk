// <copyright file="PhoneCamera.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>A phone model and the lenses / zoom presets its camera app offers.</summary>
/// <param name="Id">Stable id stored in plans, e.g. <c>iphone-16-pro</c>.</param>
/// <param name="Brand">"Apple", "Google", "Samsung", "Generic".</param>
/// <param name="Model">Display name, e.g. "iPhone 16 Pro".</param>
/// <param name="Lenses">Lenses in zoom order (widest first).</param>
public sealed record PhoneCamera(string Id, string Brand, string Model, IReadOnlyList<PhoneLens> Lenses)
{
    /// <summary>The lens with <paramref name="lensId"/>, or null.</summary>
    public PhoneLens? Lens(string? lensId) =>
        lensId is null ? null : Lenses.FirstOrDefault(l => string.Equals(l.Id, lensId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The main (1×) lens, else the first one.</summary>
    public PhoneLens MainLens => Lens("1x") ?? Lenses[0];
}
