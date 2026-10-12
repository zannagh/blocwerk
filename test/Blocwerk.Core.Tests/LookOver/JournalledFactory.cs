// <copyright file="JournalledFactory.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>A context factory whose contexts record into <paramref name="journal"/>, as the hold merge needs to be undoable.</summary>
internal sealed class JournalledFactory(string connectionString, ChangeJournal journal) : IDbContextFactory<BlocwerkDbContext>
{
    public BlocwerkDbContext CreateDbContext() => TestDb.Create(connectionString, new ChangeJournalInterceptor(journal));
}
