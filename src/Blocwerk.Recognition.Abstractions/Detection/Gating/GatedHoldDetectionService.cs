// <copyright file="GatedHoldDetectionService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Detection.Gating;

/// <summary>
/// Hold detection behind the <see cref="IImageRecognitionGate"/> and the <c>HoldDetection:Enabled</c> kill switch:
/// switched off, every photo simply has no detected holds, which every caller already handles.
/// </summary>
public sealed class GatedHoldDetectionService : IHoldDetectionService
{
    private readonly IHoldDetectionService inner;
    private readonly IImageRecognitionGate gate;
    private readonly HoldDetectionSettings settings;
    private readonly ILogger logger;

    /// <summary>Initializes a new instance of the <see cref="GatedHoldDetectionService"/> class.</summary>
    /// <param name="inner">The detector.</param>
    /// <param name="gate">The process-wide recognition gate.</param>
    /// <param name="settings">The hold detection settings (the kill switch).</param>
    /// <param name="logger">Hears about skipped runs.</param>
    public GatedHoldDetectionService(IHoldDetectionService inner, IImageRecognitionGate gate, HoldDetectionSettings settings, ILogger logger)
    {
        this.inner = inner;
        this.gate = gate;
        this.settings = settings;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public Task<List<DetectedHold>> DetectHoldsAsync(byte[] imageData, HoldDetectionParameters? parameters = null)
    {
        if (!settings.Enabled)
        {
            logger.LogInformation("Hold detection is switched off (HoldDetection:Enabled=false); the photo gets no detected holds");
            return Task.FromResult(new List<DetectedHold>());
        }

        return gate.RunAsync("detect", () => inner.DetectHoldsAsync(imageData, parameters));
    }
}
