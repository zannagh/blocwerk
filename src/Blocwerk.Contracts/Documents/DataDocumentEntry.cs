// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One document in a <see cref="DataDocumentIndex"/>.</summary>
/// <param name="Kind">The kind (<see cref="DataDocumentKinds"/>).</param>
/// <param name="Sha">The SHA-256 of its bytes, lowercase hex: the <c>/d/{kind}/{sha}</c> to fetch.</param>
/// <param name="Bytes">Its size on the wire (the Brotli copy).</param>
/// <param name="Cursor">The feed cursor read before it was built: every change at or below it is in the document.</param>
public sealed record DataDocumentEntry(string Kind, string Sha, long Bytes, string Cursor);
