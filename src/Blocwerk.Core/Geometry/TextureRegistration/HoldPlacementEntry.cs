// <copyright file="HoldPlacementEntry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// One hold placed by a <see cref="HoldPlacementRun"/>: a hash of the placement the run wrote (to detect a
/// later move) and every value it replaced or that follows from the placement (to restore them). Stored as a
/// JSON array in <see cref="HoldPlacementRun.HoldsJson"/>.
/// </summary>
public sealed record HoldPlacementEntry
{
    /// <summary>Gets the hold.</summary>
    public Guid HoldId { get; init; }

    /// <summary>Gets the hash of the facet position the run wrote.</summary>
    public string PlacementHash { get; init; } = string.Empty;

    /// <summary>Gets the replaced metric fields (size, facet, plane position, source).</summary>
    public HoldMetricSnapshot? PrevMetric { get; init; }

    /// <summary>Gets the replaced fingerprint (its rotation-free sizes follow the metric size).</summary>
    public string? PrevFingerprintJson { get; init; }

    /// <summary>Gets the hash of the fingerprint the run left behind, or null when it had none.</summary>
    public string? FingerprintHash { get; init; }

    /// <summary>Gets the footprint before the run (the refinement the run queues replaces it).</summary>
    public string? PrevFootprintMm { get; init; }

    /// <summary>Gets the protrusion before the run (likewise).</summary>
    public string? PrevProtrusionMm { get; init; }

    /// <summary>
    /// Gets the model a correction carry mapped the hold from (<see cref="Services.HoldPlacementTrigger.Correction"/>), or
    /// null for a registration run. Re-activating that model reverts the carry exactly.
    /// </summary>
    public Guid? CarriedFromModelId { get; init; }

    /// <summary>Gets a value indicating whether a revert restores <see cref="PrevVolumePlacementJson"/> (carries only).</summary>
    public bool RestoresVolumePlacement { get; init; }

    /// <summary>Gets the volume placement before the carry (see <see cref="RestoresVolumePlacement"/>).</summary>
    public string? PrevVolumePlacementJson { get; init; }

    /// <summary>Captures what a hold carries before it is placed.</summary>
    /// <param name="hold">The hold, before the write.</param>
    /// <returns>The entry, hashes still empty.</returns>
    public static HoldPlacementEntry Before(Hold hold) => new()
    {
        HoldId = hold.Id,
        PrevMetric = HoldMetricSnapshot.Of(hold),
        PrevFingerprintJson = hold.FingerprintJson,
        PrevFootprintMm = hold.FootprintMm,
        PrevProtrusionMm = hold.ProtrusionMm,
    };

    /// <summary>Hash of a hold's facet position and metric source.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>A short stable hash.</returns>
    public static string HashPlacement(Hold hold) => Hash(string.Join(
        '|',
        hold.FacetId ?? "-",
        hold.PlaneAMm?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
        hold.PlaneBMm?.ToString("R", CultureInfo.InvariantCulture) ?? "-",
        hold.MetricSource ?? "-"));

    /// <summary>Hash of a fingerprint JSON, or null for none.</summary>
    /// <param name="json">The fingerprint.</param>
    /// <returns>The hash.</returns>
    public static string? HashFingerprint(string? json) => json is null ? null : Hash(json);

    /// <summary>Serializes a run's entries.</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>JSON.</returns>
    public static string ToJson(IEnumerable<HoldPlacementEntry> entries) => JsonSerializer.Serialize(entries);

    /// <summary>Reads a run's entries (empty for unreadable JSON).</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The entries.</returns>
    public static List<HoldPlacementEntry> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<HoldPlacementEntry>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Restores the hold when the placement the run wrote is still there; false (and nothing changed) when
    /// it was moved, re-placed or un-placed since. A fingerprint changed since is kept.
    /// </summary>
    /// <param name="hold">The tracked hold.</param>
    /// <returns>Whether it was restored.</returns>
    public bool TryRevert(Hold hold)
    {
        if (HashPlacement(hold) != PlacementHash)
        {
            return false;
        }

        PrevMetric?.RestoreTo(hold);
        if (HashFingerprint(hold.FingerprintJson) == FingerprintHash)
        {
            hold.FingerprintJson = PrevFingerprintJson;
        }

        hold.FootprintMm = PrevFootprintMm;
        hold.ProtrusionMm = PrevProtrusionMm;
        if (RestoresVolumePlacement)
        {
            hold.VolumePlacementJson = PrevVolumePlacementJson;
        }

        return true;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);
}
