using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// Model configuration for the resumable big-wall-update aggregate (<see cref="WallUpdateSession"/> and
/// its decision child tables). Split out of the main context file, which is already long.
/// </summary>
public partial class BlocwerkDbContext
{
    /// <summary>
    /// The resumable big-wall-update aggregate: the session header plus its two decision child tables.
    /// </summary>
    /// <remarks>
    /// The decision rows CASCADE from their hold ends, deliberately against the Restrict convention the
    /// other Hold-referencing tables follow. Those (<see cref="HoldLink"/>, <see cref="HoldGenerationLink"/>,
    /// <see cref="BoulderHold"/>) are history or live structure and are prepared for deletion by
    /// <see cref="HoldDeletion"/>; these are ephemeral working state, and a staged hold deleted mid-session
    /// must take any decision about it with it rather than dangle. Cascading at the STORE also keeps
    /// <see cref="HoldDeletion"/> unchanged — it only has to clear Restrict dependants.
    /// <para>
    /// Every hold/panel end uses <c>WithMany()</c> with no principal-side navigation: that is the repo
    /// convention for these link-ish tables, and it also keeps <see cref="ChangeJournalCascadeGuard"/>
    /// (which enumerates cascade dependents via their collection navigation) from ever treating a
    /// working-state row as a reason to block a journal revert.
    /// </para>
    /// </remarks>
    private static void ConfigureWallUpdateSession(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WallUpdateSession>(entity =>
        {
            // Sessions belong to the wall aggregate; deleting the wall takes them with it.
            entity.HasOne(s => s.Wall)
                .WithMany()
                .HasForeignKey(s => s.WallId)
                .OnDelete(DeleteBehavior.Cascade);

            // The hot read: "is there an open update on this wall?" — plus newest-first history.
            entity.HasIndex(s => new { s.WallId, s.Status });
            entity.HasIndex(s => s.CreatedAt);

            // "At most one OPEN session per wall" is an INVARIANT, not a convention: the read-then-write
            // in WallBigUpdateService.ClearForStagingAsync is not atomic, so two admins tapping "Update
            // wall" in the same window can both see no open session. Only the store can settle that, so
            // it is a partial unique index over the Open status (0) — the loser's SaveChanges fails and
            // StageAsync surfaces it as the same conflict the read path produces. Promoted/Discarded rows
            // are history and stay unconstrained, which is why the filter (and not a plain unique index).
            entity.HasIndex(s => s.WallId)
                .HasDatabaseName("IX_WallUpdateSessions_WallId_Open")
                .HasFilter("\"Status\" = 0")
                .IsUnique();
        });

        modelBuilder.Entity<WallUpdateHoldDecision>(entity =>
        {
            entity.HasOne<WallUpdateSession>()
                .WithMany()
                .HasForeignKey(d => d.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Hold)
                .WithMany()
                .HasForeignKey(d => d.HoldId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.PairedHold)
                .WithMany()
                .HasForeignKey(d => d.PairedHoldId)
                .OnDelete(DeleteBehavior.Cascade);

            // One verdict per subject hold per kind: the upsert key the incremental saves use.
            entity.HasIndex(d => new { d.SessionId, d.Kind, d.HoldId }).IsUnique();
        });

        modelBuilder.Entity<WallUpdateNeighbourDecision>(entity =>
        {
            entity.HasOne<WallUpdateSession>()
                .WithMany()
                .HasForeignKey(d => d.SessionId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Panel)
                .WithMany()
                .HasForeignKey(d => d.PanelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.Hold)
                .WithMany()
                .HasForeignKey(d => d.HoldId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(d => d.CentreHold)
                .WithMany()
                .HasForeignKey(d => d.CentreHoldId)
                .OnDelete(DeleteBehavior.Cascade);

            // A panel's set is rewritten whole on each confirm, so this is a read index, not a constraint:
            // the same hold may legitimately appear as both a link end and a removal.
            entity.HasIndex(d => new { d.SessionId, d.PanelId });
        });
    }

    private static void ConfigureChangeJournal(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChangeJournalBatch>(entity =>
        {
            // Two read paths: newest-first browsing, and "everything for this aggregate".
            entity.HasIndex(b => b.CreatedAt);
            entity.HasIndex(b => new { b.ScopeKind, b.ScopeId });
        });

        modelBuilder.Entity<ChangeJournalEntry>(entity =>
        {
            // Scalar FK only (no navigation): a batch can span several SaveChanges on different
            // contexts, so entries are inserted referencing a batch row that this context need not
            // be tracking. Deleting a batch takes its entries with it.
            entity.HasOne<ChangeJournalBatch>()
                .WithMany()
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Cascade);

            // Replay reads a batch's entries strictly in order; Seq is unique within a batch.
            entity.HasIndex(e => new { e.BatchId, e.Seq }).IsUnique();
        });

        modelBuilder.Entity<JournalBlob>(entity =>
        {
            entity.HasKey(b => b.Sha256);
        });
    }
}
