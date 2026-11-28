// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>Model configuration of Sign in with Apple: linked Apple IDs and issued nonces. Strictly additive.</summary>
public partial class BlocwerkDbContext
{
    public DbSet<AppleLink> AppleLinks => Set<AppleLink>();

    public DbSet<AppleSignInNonce> AppleSignInNonces => Set<AppleSignInNonce>();

    /// <remarks>
    /// The link hangs off its user and goes with them. Both its columns are unique: one account per Apple ID, one
    /// Apple ID per account. The nonce hash is the lookup of every use; the expiry index serves the clean-up.
    /// </remarks>
    private static void ConfigureAppleSignIn(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppleLink>(entity =>
        {
            entity.HasIndex(l => l.Subject).IsUnique();
            entity.HasIndex(l => l.UserId).IsUnique();

            entity.HasOne(l => l.User)
                .WithMany()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppleSignInNonce>(entity =>
        {
            entity.HasIndex(n => n.NonceHash).IsUnique();
            entity.HasIndex(n => n.ExpiresAt);
        });
    }
}
