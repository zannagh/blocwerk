// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>One classified group of bulk write calls: a file, the entity, how many calls, and how the feed hears of them.</summary>
/// <param name="File">The source file, relative to <c>src/</c>.</param>
/// <param name="Entity">The entity written, or null for calls the scanner cannot resolve.</param>
/// <param name="Count">How many such calls the file makes; a new one changes it and fails the guard.</param>
/// <param name="Handling">Rows, resync, or excluded.</param>
/// <param name="Reason">What is written, or why nothing is.</param>
internal sealed record FeedBulkSite(string File, Type? Entity, int Count, FeedBulkHandling Handling, string Reason);
