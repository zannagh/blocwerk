// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>A session-less factory over a test database (either provider) whose contexts carry the given interceptors.</summary>
public sealed class InterceptingRootDbContextFactory : RootDbContextFactory
{
    private readonly string connectionString;
    private readonly IInterceptor[] interceptors;

    public InterceptingRootDbContextFactory(string connectionString, params IInterceptor[] interceptors)
        : base(new DbContextOptionsBuilder<BlocwerkDbContext>().Options)
    {
        this.connectionString = connectionString;
        this.interceptors = interceptors;
    }

    public override BlocwerkDbContext CreateDbContext() => TestDb.Create(connectionString, interceptors);
}
