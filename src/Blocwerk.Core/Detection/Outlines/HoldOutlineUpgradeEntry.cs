using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// One hold changed by an outline upgrade run: what the run wrote (as hashes, to detect later edits) and
/// the values it replaced (to restore them). Stored as a JSON array in <see cref="HoldOutlineUpgradeRun.HoldIdsJson"/>.
/// </summary>
public sealed record HoldOutlineUpgradeEntry
{
    /// <summary>Gets the hold.</summary>
    public Guid HoldId { get; init; }

    /// <summary>Gets the hash of the outline the run wrote, or null when it only added a fingerprint.</summary>
    public string? ShapeHash { get; init; }

    /// <summary>Gets a value indicating whether the replaced shape was an empty list rather than null.</summary>
    public bool PrevShapeEmpty { get; init; }

    /// <summary>Gets the replaced outline source.</summary>
    public HoldOutlineSource? PrevOutlineSource { get; init; }

    /// <summary>Gets the hash of the fingerprint the run left behind, or null when the run did not change it.</summary>
    public string? FingerprintHash { get; init; }

    /// <summary>Gets the fingerprint the run replaced (null when there was none).</summary>
    public string? PrevFingerprintJson { get; init; }

    /// <summary>Gets the replaced metric fields, or null when the run wrote no metric.</summary>
    public HoldMetricSnapshot? PrevMetric { get; init; }

    /// <summary>Hash of a hold's current outline (polygon + holes).</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>A short stable hash.</returns>
    public static string HashShape(Hold hold) =>
        Hash(JsonSerializer.Serialize(new { p = hold.ShapePoints, h = hold.ShapeHoles }));

    /// <summary>Hash of a fingerprint JSON, or null for none.</summary>
    /// <param name="json">The fingerprint JSON.</param>
    /// <returns>A short stable hash.</returns>
    public static string? HashFingerprint(string? json) => json is null ? null : Hash(json);

    /// <summary>Serializes a run's entries.</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>JSON.</returns>
    public static string ToJson(IEnumerable<HoldOutlineUpgradeEntry> entries) => JsonSerializer.Serialize(entries);

    /// <summary>Reads a run's entries (empty for unreadable JSON).</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The entries.</returns>
    public static List<HoldOutlineUpgradeEntry> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<HoldOutlineUpgradeEntry>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);
}
