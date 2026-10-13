// <copyright file="CopyHost.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.HoldEval;

/// <summary>What the app's services need around them to run against a database copy from the command line.</summary>
internal sealed class CopyHost(DbContextOptions<BlocwerkDbContext> options) : IDbContextFactory<BlocwerkDbContext>
{
    /// <inheritdoc/>
    public BlocwerkDbContext CreateDbContext() => new(options);

    /// <summary>The acting user: always the given one (the wall's owner).</summary>
    /// <param name="userId">The user.</param>
    /// <returns>The service.</returns>
    public ICurrentUserService ActingAs(Guid userId)
    {
        var users = Substitute.For<ICurrentUserService>();
        users.GetCurrentUserAsync().Returns(_ => LoadAsync(userId)!);
        users.GetUserByIdAsync(Arg.Any<Guid>()).Returns(call => LoadAsync(call.Arg<Guid>()));
        return users;
    }

    /// <summary>Stands for "merging is possible", so a plan proposes merges and stacks; planning never calls it.</summary>
    /// <returns>The service.</returns>
    public static IHoldDuplicateService MergesPossible() => Substitute.For<IHoldDuplicateService>();

    private async Task<User?> LoadAsync(Guid id)
    {
        await using var db = CreateDbContext();
        return await db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
    }
}
