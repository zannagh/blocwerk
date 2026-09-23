// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>One report of the photo-real view (all optional; the client sends what it knows).</summary>
public sealed record PhotoRealReport(
    string? Event,
    int? Level,
    int? Levels,
    int? Splats,
    string? Renderer,
    string? Vendor,
    int? MaxTextureSize,
    double? DeviceMemory,
    double? Dpr,
    double? PixelRatio,
    int? CanvasWidth,
    int? CanvasHeight,
    double? FrameMs,
    double? ElapsedMs,
    int? LostCount,
    int? SafeSplats,
    bool? Mobile,
    string? Detail);
