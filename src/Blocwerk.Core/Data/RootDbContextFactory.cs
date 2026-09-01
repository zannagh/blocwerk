using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// A context factory that is safe to inject into SINGLETONS, and that deliberately applies no kiosk
/// scoping.
/// </summary>
/// <remarks>
/// <see cref="IDbContextFactory{TContext}"/> is registered SCOPED so that
/// <see cref="KioskScopedDbContextFactory"/> can stamp <see cref="BlocwerkDbContext.KioskWallId"/>
/// from the current session. A singleton cannot consume a scoped service, so the handful of
/// singletons that legitimately need a context outside any session — the telemetry collector and the
/// refresh-token handler — take this type instead. Neither touches <see cref="Entities.Wall"/>
/// queries, so bypassing the kiosk gate here grants a kiosk session nothing.
/// <para>
/// New singletons should NOT reach for this by reflex: if the work is done on behalf of a session,
/// take <see cref="IDbContextFactory{TContext}"/> from a scope so the kiosk gate applies.
/// </para>
/// </remarks>
public class RootDbContextFactory : IDbContextFactory<BlocwerkDbContext>
{
    private readonly DbContextOptions<BlocwerkDbContext> options;

    public RootDbContextFactory(DbContextOptions<BlocwerkDbContext> options)
    {
        this.options = options;
    }

    /// <summary>Creates a context with no session scoping of any kind.</summary>
    /// <remarks>Virtual only so tests can substitute a SQLite-backed context for the Postgres one.</remarks>
    public virtual BlocwerkDbContext CreateDbContext()
    {
        return new BlocwerkDbContext(options);
    }
}
