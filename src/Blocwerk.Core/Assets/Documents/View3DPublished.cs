// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Assets.Documents;

/// <summary>A view as published for <c>wall3d.js</c>: the document's sha and size, and the overlay sent over interop.</summary>
/// <param name="Sha">The document's SHA-256, lowercase hex.</param>
/// <param name="Bytes">The document's size, uncompressed.</param>
/// <param name="Overlay">The boulder overlay.</param>
/// <param name="Token">The share token the view's URLs carried (left out of the document), or null.</param>
public sealed record View3DPublished(string Sha, long Bytes, Wall3DOverlay Overlay, string? Token);
