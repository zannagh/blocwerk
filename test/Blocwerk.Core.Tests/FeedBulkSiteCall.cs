// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>One <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> call found by <see cref="FeedBulkSiteScanner"/>.</summary>
/// <param name="File">The source file, relative to <c>src/</c>.</param>
/// <param name="Entity">The entity it writes, or null when the scanner cannot tell.</param>
internal sealed record FeedBulkSiteCall(string File, Type? Entity);
