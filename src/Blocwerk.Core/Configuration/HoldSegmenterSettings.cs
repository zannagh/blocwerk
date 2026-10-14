// <copyright file="HoldSegmenterSettings.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Configuration;

/// <summary>
/// The segmentation model that outlines freshly detected holds. Bound from <c>Blocwerk:HoldDetection:Segmenter:*</c>,
/// falling back to <c>HOLDDETECTION__SEGMENTER__*</c> environment variables. The model is two files that are not part
/// of the app image; while either is missing, or with <see cref="Enabled"/> false, holds are outlined by colour as before.
/// </summary>
public class HoldSegmenterSettings
{
    /// <summary>Gets or sets a value indicating whether the model is used when its files are there. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the encoder file, absolute or relative to the app.</summary>
    public string EncoderPath { get; set; } = "models/hold-segmenter-encoder.onnx";

    /// <summary>Gets or sets the decoder file, absolute or relative to the app.</summary>
    public string DecoderPath { get; set; } = "models/hold-segmenter-decoder.onnx";

    /// <summary>Gets or sets the CPU threads one inference may use. Default 4, so a shared host stays responsive.</summary>
    public int Threads { get; set; } = 4;

    /// <summary>Binds the settings from the <c>Blocwerk</c> section and the environment.</summary>
    /// <param name="section">The <c>Blocwerk</c> section.</param>
    /// <returns>The bound settings.</returns>
    public static HoldSegmenterSettings Bind(IConfiguration section)
    {
        var defaults = new HoldSegmenterSettings();
        return new HoldSegmenterSettings
        {
            Enabled = !bool.TryParse(Read(section, "Enabled", "ENABLED"), out var on) || on,
            EncoderPath = Read(section, "EncoderPath", "ENCODERPATH") ?? defaults.EncoderPath,
            DecoderPath = Read(section, "DecoderPath", "DECODERPATH") ?? defaults.DecoderPath,
            Threads = int.TryParse(Read(section, "Threads", "THREADS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads) && threads > 0
                ? threads
                : defaults.Threads,
        };
    }

    private static string? Read(IConfiguration section, string key, string env) =>
        section[$"HoldDetection:Segmenter:{key}"] ?? Environment.GetEnvironmentVariable($"HOLDDETECTION__SEGMENTER__{env}");
}
