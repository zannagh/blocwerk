// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>
/// The Postgres feed tests run alone: the snapshot horizon is cluster-wide, so another test's open transaction (in its own
/// database on the same server) would hold their deliveries back and blur what they assert about timing.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresFeedCollection
{
    public const string Name = "Postgres feed";
}
