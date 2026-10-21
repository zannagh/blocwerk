// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>The content hashes (lowercase hex) <see cref="AssetRouteFixture.SeedAsync"/> stores, one per kind.</summary>
public sealed record SeededAssets(
    string Panel,
    Guid PanelId,
    string WallPhoto,
    string ResetPhoto,
    string Gallery,
    string Texture,
    string Mask,
    string Splat,
    string SplatLod,
    string VideoThumbnail,
    string DraftVideoThumbnail,
    Guid OtherWallId,
    string OtherWallPanel);
