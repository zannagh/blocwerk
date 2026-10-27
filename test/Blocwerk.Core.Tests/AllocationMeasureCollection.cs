// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>Runs alone: it measures the whole process's allocations.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasureCollection
{
    public const string Name = "Allocation measurements";
}
