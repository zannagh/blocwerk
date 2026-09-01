using Blocwerk.Core.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Data;

/// <summary>
/// The scoped <see cref="IDbContextFactory{TContext}"/> every session resolves. It creates the
/// context exactly as EF would, then stamps <see cref="BlocwerkDbContext.KioskWallId"/> from the
/// current <see cref="IKioskContext"/>.
/// </summary>
/// <remarks>
/// This is THE enforcement point for "a kiosk session may only reach its own wall". Services set
/// <see cref="BlocwerkDbContext.CurrentUserId"/> by hand in roughly forty places; relying on the
/// same discipline for the kiosk gate would mean one forgotten line is a silent authorisation hole.
/// Stamping at creation makes every context — including the ones created inside a service that has
/// never heard of kiosks — carry the restriction.
/// <para>
/// Both context-creation paths funnel through here: callers that inject
/// <see cref="IDbContextFactory{TContext}"/> directly, and the plain scoped
/// <see cref="BlocwerkDbContext"/> registration, which is itself built by resolving this factory
/// from the same scope.
/// </para>
/// </remarks>
public sealed class KioskScopedDbContextFactory : IDbContextFactory<BlocwerkDbContext>
{
    private readonly RootDbContextFactory inner;
    private readonly IKioskContext? kioskContext;

    /// <summary>Creates the factory used by every scoped consumer.</summary>
    /// <param name="inner">Creates the raw, unstamped context.</param>
    /// <param name="kioskContext">
    /// Absent in hosts that have no HTTP layer (tests, tooling), which simply means "never a kiosk".
    /// </param>
    public KioskScopedDbContextFactory(RootDbContextFactory inner, IKioskContext? kioskContext = null)
    {
        this.inner = inner;
        this.kioskContext = kioskContext;
    }

    public BlocwerkDbContext CreateDbContext()
    {
        var context = inner.CreateDbContext();

        // Read defensively: a kiosk context that throws must not hand out an UNSTAMPED context, so
        // the failure closes the session down to no wall at all rather than opening it to every wall.
        try
        {
            if (kioskContext is { IsKiosk: true })
            {
                context.KioskWallId = kioskContext.KioskWallId ?? Guid.Empty;
            }
        }
        catch
        {
            context.KioskWallId = Guid.Empty;
            throw;
        }

        return context;
    }
}
