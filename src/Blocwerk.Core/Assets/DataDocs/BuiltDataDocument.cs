// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>One build of a data document.</summary>
/// <param name="Json">The canonical bytes the sha names.</param>
/// <param name="People">
/// The users the document names by byline (the boulders' creators and setters, the comments' authors; empty for the
/// other kinds). Kept beside the document, never in it: a <c>user.profile</c> row of one of them rebuilds it, whichever
/// wall the row was written for.
/// </param>
/// <param name="Consented">
/// For a document that names members by their kiosk consent (the kiosk class's comments), those of
/// <paramref name="People"/> it named; null when consent decides nothing in it. A change to that set rebuilds it.
/// </param>
public sealed record BuiltDataDocument(byte[] Json, IReadOnlyCollection<Guid> People, IReadOnlyCollection<Guid>? Consented = null);
