// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One image of a <see cref="GalleryDocument"/>.</summary>
/// <param name="Id">The image.</param>
/// <param name="Sha">Its sha, or null while it is not hashed yet (network only).</param>
/// <param name="Type">Its content type.</param>
/// <param name="Bytes">Its size.</param>
/// <param name="Caption">Its caption, if any.</param>
/// <param name="CapturedAt">When it was captured.</param>
public sealed record GalleryDocumentImage(Guid Id, string? Sha, string Type, long Bytes, string? Caption, DateTimeOffset CapturedAt);
