// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Stamps the blobs' content hashes before every save (see <see cref="ContentHashStamp"/>).</summary>
public partial class BlocwerkDbContext
{
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ContentHashStamp.Apply(ChangeTracker);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ContentHashStamp.Apply(ChangeTracker);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
