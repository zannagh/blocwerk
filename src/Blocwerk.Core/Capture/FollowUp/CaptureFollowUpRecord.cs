// <copyright file="CaptureFollowUpRecord.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>
/// What the post-capture chain did, stored on <see cref="Entities.WallCapture.FollowUpJson"/>: one entry per
/// step that ran (the chain's resume point), plus an optional note about the capture's photo-real view.
/// </summary>
/// <param name="Steps">The recorded steps, in the order they ran.</param>
/// <param name="Note">A plain-words note for the admin (e.g. why there is no photo-real view), or null.</param>
public sealed record CaptureFollowUpRecord(
    [property: JsonPropertyName("steps")] IReadOnlyList<CaptureFollowUpEntry> Steps,
    [property: JsonPropertyName("note")] string? Note = null)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Nothing recorded yet.</summary>
    public static CaptureFollowUpRecord Empty { get; } = new([]);

    /// <summary>Reads a stored record; empty when there is none or it does not parse.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The record.</returns>
    public static CaptureFollowUpRecord Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            var record = JsonSerializer.Deserialize<CaptureFollowUpRecord>(json, Json);
            return record is null ? Empty : record with { Steps = record.Steps ?? [] };
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>The record as stored.</summary>
    /// <returns>JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>The step's entry, or null when it has not run.</summary>
    /// <param name="key">The step key.</param>
    /// <returns>The entry.</returns>
    public CaptureFollowUpEntry? Find(string key) => Steps.FirstOrDefault(s => s.Key == key);

    /// <summary>The record with <paramref name="entry"/> added (replacing an earlier entry of the same step).</summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The new record.</returns>
    public CaptureFollowUpRecord With(CaptureFollowUpEntry entry) =>
        this with { Steps = [.. Steps.Where(s => s.Key != entry.Key), entry] };
}
