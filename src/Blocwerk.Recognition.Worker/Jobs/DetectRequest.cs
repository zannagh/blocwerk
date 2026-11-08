// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Configuration;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>Contracts <c>jobs/recognition/detect-request</c>: <c>holds-job</c> plus the model by hash.</summary>
public sealed record DetectRequest(HoldDetectionTilingSettings Tiling, double WholeImageConfidence, double WindowIoU, DetectRequestModel Model);
