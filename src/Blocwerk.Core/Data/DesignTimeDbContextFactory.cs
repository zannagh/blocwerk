using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Blocwerk.Core.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BlocwerkDbContext>
{
    public BlocwerkDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<BlocwerkDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Port=5432;Database=blocwerk;Username=postgres;Password=blocwerk_dev");
        return new BlocwerkDbContext(optionsBuilder.Options);
    }
}
