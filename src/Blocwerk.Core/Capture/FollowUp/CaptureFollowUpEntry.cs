// <copyright file="CaptureFollowUpEntry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>One recorded step.</summary>
/// <param name="Key">The step (<see cref="ICaptureFollowUpStep.Key"/>).</param>
/// <param name="Outcome">What it did.</param>
/// <param name="Summary">Its one-line summary (for a failure: what failed).</param>
/// <param name="At">When it finished.</param>
/// <param name="SplatId">The photo-real view it ran against (photo-real steps only).</param>
/// <param name="InputsKey">What it read besides the photos (<see cref="ICaptureFollowUpStep.InputsKeyAsync"/>).</param>
public sealed record CaptureFollowUpEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("outcome")] CaptureFollowUpOutcome Outcome,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("splatId")] Guid? SplatId = null,
    [property: JsonPropertyName("inputs")] string? InputsKey = null);
