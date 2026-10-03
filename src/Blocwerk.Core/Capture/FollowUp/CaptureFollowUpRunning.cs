// <copyright file="CaptureFollowUpRunning.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Capture.FollowUp;

/// <summary>The step that is running right now (written before it starts, dropped when its entry is recorded).</summary>
/// <param name="Key">The step (<see cref="ICaptureFollowUpStep.Key"/>).</param>
/// <param name="Title">Its title, as the admin sees it.</param>
/// <param name="StartedAt">When it started.</param>
public sealed record CaptureFollowUpRunning(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt);
