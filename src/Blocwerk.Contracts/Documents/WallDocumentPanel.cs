// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One live panel of a <see cref="WallDocument"/>; its photo is fetched by sha through <c>/a/{sha}</c>.</summary>
/// <param name="Id">The panel.</param>
/// <param name="Col">Its grid column.</param>
/// <param name="Row">Its grid row.</param>
/// <param name="Generation">The panel's own generation (live holds are per panel).</param>
/// <param name="Sha">The photo's sha, or null while it is not hashed yet (network only).</param>
/// <param name="Type">The photo's content type.</param>
public sealed record WallDocumentPanel(Guid Id, int Col, int Row, int Generation, string? Sha, string Type);
