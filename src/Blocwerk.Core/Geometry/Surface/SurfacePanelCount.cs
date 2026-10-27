// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.Surface;

/// <summary>Per panel photo: holds checked, placed from the surface (own view / a linked copy's view), by the fallback, unchanged.</summary>
public sealed record SurfacePanelCount(Guid PanelId, int Checked, int OwnView, int SharedView, int Fallback, int Missed);
