using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The test stand-in for <c>KioskScopedDbContextFactory</c>: stamps every context with the
/// tablet's wall, exactly as production does. Without it these tests would exercise the
/// anonymous branch with no wall gate behind it at all.
/// </summary>
internal sealed class KioskStampingFactory : IDbContextFactory<BlocwerkDbContext>
{
    private readonly IDbContextFactory<BlocwerkDbContext> inner;
    private readonly IKioskContext kioskContext;

    public KioskStampingFactory(IDbContextFactory<BlocwerkDbContext> inner, IKioskContext kioskContext)
    {
        this.inner = inner;
        this.kioskContext = kioskContext;
    }

    public BlocwerkDbContext CreateDbContext()
    {
        var db = inner.CreateDbContext();
        if (kioskContext.IsKiosk)
        {
            db.KioskWallId = kioskContext.KioskWallId ?? Guid.Empty;
        }

        return db;
    }
}
