// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>A session-less factory over a test database (either provider) whose contexts carry the given interceptors.</summary>
public sealed class InterceptingRootDbContextFactory(string connectionString, params IInterceptor[] interceptors)
    : RootDbContextFactory(new DbContextOptionsBuilder<BlocwerkDbContext>().Options)
{
    public override BlocwerkDbContext CreateDbContext() => TestDb.Create(connectionString, interceptors);
}
