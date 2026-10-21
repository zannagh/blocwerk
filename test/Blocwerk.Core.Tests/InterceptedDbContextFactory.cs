// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Tests;

/// <summary>A context factory whose contexts carry the given interceptors, for a service under test.</summary>
internal sealed class InterceptedDbContextFactory : IDbContextFactory<BlocwerkDbContext>
{
    private readonly string connectionString;
    private readonly IInterceptor[] interceptors;

    public InterceptedDbContextFactory(string connectionString, IInterceptor[] interceptors)
    {
        this.connectionString = connectionString;
        this.interceptors = interceptors;
    }

    public BlocwerkDbContext CreateDbContext() => TestDb.Create(connectionString, interceptors);
}
