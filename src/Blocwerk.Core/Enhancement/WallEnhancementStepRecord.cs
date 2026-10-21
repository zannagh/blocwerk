// <copyright file="WallEnhancementStepRecord.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Blocwerk.Core.Enhancement;

/// <summary>How a step of a wall enhancement ended.</summary>
public enum WallEnhancementStepOutcomeKind
{
    /// <summary>Started and not finished (or interrupted by a restart: it runs again).</summary>
    Running = 0,

    /// <summary>Ran.</summary>
    Done = 1,

    /// <summary>Could not run here (no 3D model, switched off on this server).</summary>
    Skipped = 2,

    /// <summary>Threw; the next steps still ran.</summary>
    Failed = 3,
}

/// <summary>
/// One step's entry in <see cref="Entities.WallEnhancementRun.StepsJson"/>: saved when the step starts (with
/// <see cref="StartedAt"/>, which a resumed step keeps, so the batches its first attempt wrote are still found) and
/// again when it ends, with the journal batches it wrote.
/// </summary>
/// <param name="Key">The step key.</param>
/// <param name="Title">The step's title when it ran.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Summary">Its one-line summary.</param>
/// <param name="StartedAt">When it first started.</param>
/// <param name="FinishedAt">When it ended, or null while running.</param>
/// <param name="BatchIds">The journal batches it wrote, oldest first.</param>
/// <param name="NeverRan">True for a step a cancel stopped before it started: it failed without doing anything.</param>
public sealed record WallEnhancementStepRecord(
    string Key,
    string Title,
    WallEnhancementStepOutcomeKind Outcome,
    string Summary,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<Guid> BatchIds,
    bool NeverRan = false)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>True once the step ended (in any way): a resumed run does not run it again.</summary>
    public bool IsFinished => FinishedAt is not null && Outcome != WallEnhancementStepOutcomeKind.Running;

    /// <summary>Reads the run's step list.</summary>
    /// <param name="json">The stored JSON.</param>
    /// <returns>The records (empty for bad or missing JSON).</returns>
    public static List<WallEnhancementStepRecord> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<WallEnhancementStepRecord>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes the step list.</summary>
    /// <param name="records">The records.</param>
    /// <returns>The JSON.</returns>
    public static string Write(IEnumerable<WallEnhancementStepRecord> records) => JsonSerializer.Serialize(records.ToList(), Json);

    /// <summary>The list with <paramref name="record"/> in place of the entry of its key (or appended).</summary>
    /// <param name="records">The list.</param>
    /// <param name="record">The new entry.</param>
    /// <returns>The new list.</returns>
    public static List<WallEnhancementStepRecord> With(IEnumerable<WallEnhancementStepRecord> records, WallEnhancementStepRecord record)
    {
        var list = records.Where(r => r.Key != record.Key).ToList();
        list.Add(record);
        return list;
    }
}
